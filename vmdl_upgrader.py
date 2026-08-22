#!/usr/bin/env python3
"""
VMDL Upgrader & CSWin64 Compiler Pipeline for CSDK12 / Source 2 ModelDoc

Workflow:
1. Upgrades format:modeldoc40 -> format:modeldoc41 and injects:
   - NmSkeletonList (with NmSkeletonReference)
   - AnimGraph2List (with DefaultAnimGraph2 and UI AnimGraph2)
2. Maps CSDK12 content path to CSWin64 content folder (citadel_addons -> csgo_addons).
3. Compiles via CSWin64 resourcecompiler.exe -f.
4. Auto-detects the matching game directory in CSDK12 and copies the generated .vmdl_c file directly into it.
5. Reverts the original .vmdl file back to the pre-upgrade format so it can continue to be edited in CSDK12 ModelDoc!
"""

import os
import sys
import argparse
import shutil
import subprocess
import re
import json

MODELDOC41_HEADER = '<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc41:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->'

DEFAULT_CSWIN_DIR = r"A:\modding\CSWin64"
DEFAULT_CITADEL_ADDONS_DIR = ""

DEFAULT_CONFIG = {
    "cswin_dir": DEFAULT_CSWIN_DIR,
    "citadel_addons_dir": DEFAULT_CITADEL_ADDONS_DIR,
    "last_target_path": "",
    "chk_compile": True,
    "chk_revert": True,
    "chk_header": True,
    "chk_skel": True,
    "chk_graph": True,
    "chk_ui_graph": True,
    "chk_backup": True
}


def get_config_path():
    """Returns absolute path to config.json in script/executable directory."""
    if hasattr(sys, '_MEIPASS'):
        base_dir = os.path.dirname(sys.executable)
    else:
        base_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base_dir, 'config.json')


def is_temporary_path(path):
    """Checks if a path is inside a temporary directory."""
    if not path:
        return False
    norm = os.path.normpath(path).lower()
    temp_dir = os.path.normpath(os.environ.get('TEMP', 'C:\\Temp')).lower()
    return norm.startswith(temp_dir) or 'appdata\\local\\temp' in norm or 'citadel_test_' in norm or 'hero_filter_test_' in norm


def load_config():
    """Loads settings from config.json with fallback defaults."""
    cfg = DEFAULT_CONFIG.copy()
    path = get_config_path()
    if os.path.exists(path):
        try:
            with open(path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                if isinstance(data, dict):
                    cfg.update(data)
        except Exception:
            pass

    # Sanitize citadel_addons_dir: do not keep temp paths or deleted folders
    citadel_dir = cfg.get("citadel_addons_dir", "")
    if is_temporary_path(citadel_dir) or (citadel_dir and not os.path.exists(citadel_dir)):
        cfg["citadel_addons_dir"] = ""

    last_target = cfg.get("last_target_path", "")
    if is_temporary_path(last_target):
        cfg["last_target_path"] = ""

    return cfg


def save_config(config_dict):
    """Saves settings dictionary to config.json."""
    path = get_config_path()
    try:
        cfg = load_config()
        # Filter out temp paths
        clean_updates = {}
        for k, v in config_dict.items():
            if isinstance(v, str) and is_temporary_path(v):
                clean_updates[k] = ""
            else:
                clean_updates[k] = v
        cfg.update(clean_updates)
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(cfg, f, indent=2, ensure_ascii=False)
        return True
    except Exception as e:
        print(f"Warning: Failed to save config: {e}", file=sys.stderr)
        return False


def update_config(**kwargs):
    """Convenience function to update specific config keys."""
    return save_config(kwargs)


def extract_citadel_addons_dir(filepath):
    """
    Extracts the citadel_addons directory from a given file or folder path if present.
    Example: 'C:/Deadlock/content/citadel_addons/my_mod/models/hero.vmdl'
             -> 'C:/Deadlock/content/citadel_addons'
    """
    clean = filepath.replace('\\', '/')
    match = re.search(r'^(.*?/content/(citadel_addons|citadel_community_addons|citadel))(/|$)', clean, re.IGNORECASE)
    if match:
        return os.path.normpath(match.group(1))
    
    match2 = re.search(r'^(.*?/citadel_addons)(/|$)', clean, re.IGNORECASE)
    if match2:
        return os.path.normpath(match2.group(1))
        
    return None


def detect_hero_from_path(filepath):
    """
    Checks if a .vmdl file path belongs to a known hero in HERO_DATABASE.
    Returns the matching hero key (e.g. 'vampirebat', 'bookworm', 'haze') or None.
    """
    clean = filepath.replace('\\', '/').lower()
    parts = [p for p in clean.split('/') if p]
    if not parts:
        return None

    # Check parent folder names from closest folder upwards to root
    folder_parts = parts[:-1]
    for folder in reversed(folder_parts):
        if folder in HERO_DATABASE:
            return folder

    # Check filename stem
    stem = os.path.splitext(parts[-1])[0]
    if stem in HERO_DATABASE:
        return stem

    return None


def scan_hero_models(search_path):
    """
    Scans search_path for .vmdl files strictly located in models/heroes_wip or models/heroes_staging,
    and ONLY includes models that match known heroes in HERO_DATABASE (e.g. vampirebat, bookworm, etc.).
    """
    results = []
    if not search_path or not os.path.exists(search_path):
        return results

    search_path = os.path.abspath(search_path)

    for root, _, files in os.walk(search_path):
        clean_root = root.replace('\\', '/').lower()
        
        # Must be within heroes_wip or heroes_staging
        if 'heroes_wip' not in clean_root and 'heroes_staging' not in clean_root:
            continue

        for file in files:
            if not file.lower().endswith('.vmdl'):
                continue

            full_path = os.path.normpath(os.path.join(root, file))
            hero_name = detect_hero_from_path(full_path)
            
            # Filter: ONLY known heroes from HERO_DATABASE
            if not hero_name:
                continue

            container, addon_name, subpath = parse_csdk_path(full_path, citadel_addons_dir=search_path)
            
            if addon_name and addon_name != 'addon':
                display = f"[{addon_name}] {subpath} ({hero_name})"
            else:
                display = f"{subpath} ({hero_name})"

            results.append({
                "display": display,
                "hero": hero_name,
                "full_path": full_path,
                "addon": addon_name,
                "subpath": subpath,
                "filename": file
            })

    results.sort(key=lambda x: (x["hero"], x["display"].lower()))
    return results


def load_hero_database():
    """
    Loads hero skeleton and anim graph paths dynamically from hero_paths.json.
    Supports standard directory as well as PyInstaller _MEIPASS environment.
    """
    possible_paths = []
    
    if hasattr(sys, '_MEIPASS'):
        possible_paths.append(os.path.join(sys._MEIPASS, 'hero_paths.json'))
        possible_paths.append(os.path.join(sys._MEIPASS, 'tools', 'hero_paths.json'))
        
    script_dir = os.path.dirname(os.path.abspath(__file__))
    possible_paths.append(os.path.join(script_dir, 'hero_paths.json'))
    possible_paths.append(os.path.join(script_dir, '..', 'tools', 'hero_paths.json'))
    possible_paths.append('hero_paths.json')

    for p in possible_paths:
        if os.path.exists(p):
            try:
                with open(p, 'r', encoding='utf-8') as f:
                    return json.load(f)
            except Exception:
                pass
    return {}


HERO_DATABASE = load_hero_database()


def derive_default_paths(vmdl_path):
    """
    Looks up exact skeleton and anim graph paths from HERO_DATABASE (hero_paths.json).
    Prioritizes parent folder names in the directory path over the file name
    so custom renamed .vmdl files (e.g. models/heroes_staging/haze/custom_mesh.vmdl)
    still auto-detect their hero correctly!
    """
    vmdl_path_clean = vmdl_path.replace('\\', '/')
    filename = os.path.basename(vmdl_path_clean)
    stem = os.path.splitext(filename)[0].lower()
    
    parts = [p.lower() for p in vmdl_path_clean.split('/') if p]

    # 1. Search parent folders from closest folder upwards to root
    folder_parts = parts[:-1] if len(parts) > 1 else []
    for folder in reversed(folder_parts):
        if folder in HERO_DATABASE:
            entry = HERO_DATABASE[folder]
            return entry["skel"], entry["graph"], entry["ui_graph"]

    # 2. Check filename stem if no parent folder matched a hero
    if stem in HERO_DATABASE:
        entry = HERO_DATABASE[stem]
        return entry["skel"], entry["graph"], entry["ui_graph"]

    # 3. Direct fallback
    skel_path = re.sub(r'\.vmdl$', '.vnmskel', vmdl_path_clean, flags=re.IGNORECASE)
    graph_path = f"animgraphs/animgraph2/hero/hero.vnmgraph+{stem}.vnmgraph"
    ui_graph_path = f"animgraphs/animgraph2/hero/hero_ui.vnmgraph+{stem}.vnmgraph"
    
    return skel_path, graph_path, ui_graph_path


def upgrade_vmdl_content(content, skel_path, graph_path, ui_graph_path=None, add_skel=True, add_graph=True, add_ui_graph=True, upgrade_header=True):
    lines = content.splitlines(keepends=True)
    changes = []
    
    if upgrade_header and len(lines) > 0:
        if 'format:modeldoc40' in lines[0] or 'modeldoc' in lines[0]:
            if lines[0].strip() != MODELDOC41_HEADER:
                lines[0] = MODELDOC41_HEADER + ('\r\n' if lines[0].endswith('\r\n') else '\n')
                changes.append("Upgraded header format to modeldoc41")
    
    full_text = "".join(lines)
    
    has_nm_skel = 'NmSkeletonList' in full_text
    has_anim_graph = 'AnimGraph2List' in full_text or 'DefaultAnimGraph2' in full_text
    
    nodes_to_inject = []
    
    if add_skel and not has_nm_skel:
        skel_block = f"""\t\t\t{{
\t\t\t\t_class = "NmSkeletonList"
\t\t\t\tchildren = 
\t\t\t\t[
\t\t\t\t\t{{
\t\t\t\t\t\t_class = "NmSkeletonReference"
\t\t\t\t\t\tfilename = "{skel_path}"
\t\t\t\t\t}},
\t\t\t\t]
\t\t\t}},
"""
        nodes_to_inject.append(('NmSkeletonList', skel_block))
        
    if add_graph and not has_anim_graph:
        anim_children = f"""\t\t\t\t\t{{
\t\t\t\t\t\t_class = "DefaultAnimGraph2"
\t\t\t\t\t\tfilename = "{graph_path}"
\t\t\t\t\t}},
"""
        if add_ui_graph and ui_graph_path:
            anim_children += f"""\t\t\t\t\t{{
\t\t\t\t\t\t_class = "AnimGraph2"
\t\t\t\t\t\tname = "ui"
\t\t\t\t\t\tfilename = "{ui_graph_path}"
\t\t\t\t\t}},
"""
        graph_block = f"""\t\t\t{{
\t\t\t\t_class = "AnimGraph2List"
\t\t\t\tchildren = 
\t\t\t\t[
{anim_children}\t\t\t\t]
\t\t\t}},
"""
        nodes_to_inject.append(('AnimGraph2List', graph_block))
        
    if not nodes_to_inject:
        return "".join(lines), changes

    insert_idx = -1
    for i, line in enumerate(lines):
        if 'model_archetype' in line or 'primary_associated_entity' in line:
            if i > 0 and lines[i-1].strip() == ']':
                insert_idx = i - 1
            else:
                insert_idx = i
            break

    if insert_idx != -1:
        to_add = []
        for node_name, node_text in nodes_to_inject:
            to_add.append(node_text)
            changes.append(f"Injected {node_name} node")
        lines[insert_idx:insert_idx] = to_add
    else:
        changes.append("Error: Could not locate rootNode children closing bracket")

    return "".join(lines), changes


def parse_csdk_path(csdk_path, citadel_addons_dir=None):
    clean = csdk_path.replace('\\', '/')
    
    # 1. Standard pattern: .../content/(citadel_addons|citadel_community_addons|citadel)/<addon_name>/<subpath>
    m = re.search(r'content/(citadel_addons|citadel_community_addons|citadel)/([^/]+)/(.+)$', clean, re.IGNORECASE)
    if m:
        return m.group(1), m.group(2), m.group(3)
    
    # 2. General content subfolder pattern: .../content/<addon_name>/<subpath>
    m2 = re.search(r'content/([^/]+)/(.+)$', clean, re.IGNORECASE)
    if m2:
        return 'citadel_addons', m2.group(1), m2.group(2)
        
    # 3. Relative to configured citadel_addons_dir
    if citadel_addons_dir:
        clean_addons = citadel_addons_dir.replace('\\', '/').rstrip('/')
        if clean.lower().startswith(clean_addons.lower() + '/'):
            rel = clean[len(clean_addons) + 1:]
            parts = rel.split('/', 1)
            if len(parts) == 2:
                return 'citadel_addons', parts[0], parts[1]
            return 'citadel_addons', 'addon', parts[0]
            
    return 'citadel_addons', 'addon', os.path.basename(clean)


def compile_via_cswin_and_deploy(csdk12_vmdl_path, upgraded_vmdl_content, cswin_dir=None, citadel_addons_dir=None):
    cfg = load_config()
    use_cswin_dir = cswin_dir if cswin_dir else cfg.get("cswin_dir", DEFAULT_CSWIN_DIR)
    use_citadel_dir = citadel_addons_dir if citadel_addons_dir else cfg.get("citadel_addons_dir", "")
    
    rc_exe = os.path.join(use_cswin_dir, "game", "bin", "win64", "resourcecompiler.exe")
    cswin_game_dir = os.path.join(use_cswin_dir, "game", "csgo")

    if not os.path.exists(rc_exe):
        rc_exe_alt = os.path.join(use_cswin_dir, "bin", "win64", "resourcecompiler.exe")
        if os.path.exists(rc_exe_alt):
            rc_exe = rc_exe_alt
        else:
            return False, f"CSWin64 resourcecompiler.exe not found in: {use_cswin_dir}"

    container, addon_name, subpath = parse_csdk_path(csdk12_vmdl_path, citadel_addons_dir=use_citadel_dir)
    
    cswin_vmdl_path = os.path.join(use_cswin_dir, "content", "csgo_addons", addon_name, subpath).replace('/', '\\')
    os.makedirs(os.path.dirname(cswin_vmdl_path), exist_ok=True)
    
    with open(cswin_vmdl_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(upgraded_vmdl_content)

    cmd = [rc_exe, "-f", "-i", cswin_vmdl_path, "-game", cswin_game_dir]
    res = subprocess.run(cmd, capture_output=True, text=True)
    
    if res.returncode != 0:
        return False, f"CSWin64 Compiler error (code {res.returncode}): {res.stderr or res.stdout}"
        
    cswin_compiled_vmdlc = os.path.join(use_cswin_dir, "game", "csgo_addons", addon_name, subpath + '_c').replace('/', '\\')
    if not os.path.exists(cswin_compiled_vmdlc):
        return False, f"Compiler succeeded but .vmdl_c not found at: {cswin_compiled_vmdlc}"

    clean_path = csdk12_vmdl_path.replace('\\', '/')
    if '/content/' in clean_path.lower():
        csdk12_root = clean_path[:clean_path.lower().index('/content/')]
        csdk12_game_vmdlc = os.path.join(csdk12_root, "game", container, addon_name, subpath + '_c').replace('/', '\\')
    elif use_citadel_dir and '/content/' in use_citadel_dir.replace('\\', '/').lower():
        clean_citadel = use_citadel_dir.replace('\\', '/')
        csdk12_root = clean_citadel[:clean_citadel.lower().index('/content/')]
        csdk12_game_vmdlc = os.path.join(csdk12_root, "game", container, addon_name, subpath + '_c').replace('/', '\\')
    else:
        # Direct fallback alongside source file
        csdk12_game_vmdlc = os.path.splitext(csdk12_vmdl_path)[0] + '.vmdl_c'

    os.makedirs(os.path.dirname(csdk12_game_vmdlc), exist_ok=True)
    shutil.copyfile(cswin_compiled_vmdlc, csdk12_game_vmdlc)
    
    return True, f"Compiled via CSWin64 & deployed .vmdl_c to: {csdk12_game_vmdlc}"


def process_vmdl_file(filepath, skel_path=None, graph_path=None, ui_graph_path=None, create_backup=True, add_skel=True, add_graph=True, add_ui_graph=True, upgrade_header=True, compile_cswin=True, revert_vmdl=True, cswin_dir=None, citadel_addons_dir=None):
    filepath = os.path.abspath(filepath)
    if not os.path.exists(filepath):
        return False, f"File not found: {filepath}"

    def_skel, def_graph, def_ui_graph = derive_default_paths(filepath)
    use_skel_path = skel_path if skel_path else def_skel
    use_graph_path = graph_path if graph_path else def_graph
    use_ui_graph_path = ui_graph_path if ui_graph_path else def_ui_graph

    with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
        orig_content = f.read()

    upgraded_content, changes = upgrade_vmdl_content(
        orig_content,
        skel_path=use_skel_path,
        graph_path=use_graph_path,
        ui_graph_path=use_ui_graph_path,
        add_skel=add_skel,
        add_graph=add_graph,
        add_ui_graph=add_ui_graph,
        upgrade_header=upgrade_header
    )

    if create_backup:
        bak_file = filepath + '.bak'
        shutil.copyfile(filepath, bak_file)

    step_log = []

    if compile_cswin:
        comp_success, comp_msg = compile_via_cswin_and_deploy(
            filepath,
            upgraded_content,
            cswin_dir=cswin_dir,
            citadel_addons_dir=citadel_addons_dir
        )
        if not comp_success:
            return False, f"CSWin64 Compilation Failed: {comp_msg}"
        step_log.append(comp_msg)

    if revert_vmdl:
        with open(filepath, 'w', encoding='utf-8', newline='\n') as f:
            f.write(orig_content)
        step_log.append("Reverted CSDK12 VMDL to pre-upgrade format (ModelDoc compatible)")
    else:
        with open(filepath, 'w', encoding='utf-8', newline='\n') as f:
            f.write(upgraded_content)
        step_log.append(f"Saved upgraded VMDL ({', '.join(changes)})")

    return True, " | ".join(step_log)


def main():
    cfg = load_config()
    default_cswin = cfg.get("cswin_dir", DEFAULT_CSWIN_DIR)
    default_citadel = cfg.get("citadel_addons_dir", DEFAULT_CITADEL_ADDONS_DIR)

    parser = argparse.ArgumentParser(description="Upgrade CSDK12 .vmdl files, compile via CSWin64 resourcecompiler, deploy .vmdl_c to CSDK12 game, and revert .vmdl to pre-upgrade state.")
    parser.add_argument('--file', '-f', help="Path to a single .vmdl file")
    parser.add_argument('--dir', '-d', help="Directory containing .vmdl files to recursively process")
    parser.add_argument('--cswin-dir', default=default_cswin, help=f"Custom directory path to CSWin64 installation (current default: {default_cswin})")
    parser.add_argument('--citadel-dir', default=default_citadel, help=f"Path to CSDK12 citadel_addons directory (current default: {default_citadel or 'not set'})")
    parser.add_argument('--save-config', action='store_true', help="Save specified --cswin-dir and --citadel-dir to config.json for future runs")
    parser.add_argument('--hero', '-hp', help="Specify a hero path preset name (e.g., haze, lash, abrams, bebop, dynamo, inferno, etc.)")
    parser.add_argument('--skel', help="Custom .vnmskel path reference")
    parser.add_argument('--graph', help="Custom DefaultAnimGraph2 path reference")
    parser.add_argument('--ui-graph', help="Custom UI AnimGraph2 path reference")
    parser.add_argument('--no-backup', action='store_true', help="Disable .bak file creation")
    parser.add_argument('--no-compile', action='store_true', help="Disable CSWin64 compilation and deployment")
    parser.add_argument('--no-revert', action='store_true', help="Keep upgraded format in .vmdl instead of reverting")
    parser.add_argument('--no-header', action='store_true', help="Skip upgrading header to modeldoc41")
    parser.add_argument('--no-skel', action='store_true', help="Skip injecting NmSkeletonList node")
    parser.add_argument('--no-graph', action='store_true', help="Skip injecting AnimGraph2List node")
    parser.add_argument('--no-ui-graph', action='store_true', help="Skip including UI AnimGraph2 node")

    args = parser.parse_args()

    if args.save_config:
        update_config(cswin_dir=args.cswin_dir, citadel_addons_dir=args.citadel_dir)
        print(f"Saved configuration to {get_config_path()}:")
        print(f"  CSWin64 Path: {args.cswin_dir}")
        print(f"  Citadel Addons Path: {args.citadel_dir}")

    if not args.file and not args.dir:
        if not args.save_config:
            parser.print_help()
        sys.exit(0)

    skel_override = args.skel
    graph_override = args.graph
    ui_graph_override = args.ui_graph

    if args.hero:
        hero_key = args.hero.lower().strip()
        if hero_key in HERO_DATABASE:
            preset = HERO_DATABASE[hero_key]
            skel_override = skel_override or preset["skel"]
            graph_override = graph_override or preset["graph"]
            ui_graph_override = ui_graph_override or preset["ui_graph"]
            print(f"Using official hero paths: '{hero_key}'")

    targets = []
    if args.file:
        targets.append(args.file)

    if args.dir:
        for root, _, files in os.walk(args.dir):
            for file in files:
                if file.lower().endswith('.vmdl'):
                    targets.append(os.path.join(root, file))

    print(f"Found {len(targets)} VMDL file(s) to process.")
    for target in targets:
        success, msg = process_vmdl_file(
            target,
            skel_path=skel_override,
            graph_path=graph_override,
            ui_graph_path=ui_graph_override,
            create_backup=not args.no_backup,
            add_skel=not args.no_skel,
            add_graph=not args.no_graph,
            add_ui_graph=not args.no_ui_graph,
            upgrade_header=not args.no_header,
            compile_cswin=not args.no_compile,
            revert_vmdl=not args.no_revert,
            cswin_dir=args.cswin_dir,
            citadel_addons_dir=args.citadel_dir
        )
        status = "SUCCESS" if success else "FAILED"
        print(f"[{status}] {target}\n        {msg}")


if __name__ == '__main__':
    main()

