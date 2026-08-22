#!/usr/bin/env python3
"""
Desktop GUI for VMDL Upgrader & CSWin64 Compilation Tool (CSDK12)
Professional Dark Interface with Multithreading & Hero Auto-Discovery
"""

import os
import sys
import threading
import queue
import subprocess
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

try:
    from vmdl_upgrader import (
        process_vmdl_file,
        derive_default_paths,
        detect_hero_from_path,
        HERO_DATABASE,
        DEFAULT_CSWIN_DIR,
        DEFAULT_CITADEL_ADDONS_DIR,
        load_config,
        save_config,
        update_config,
        extract_citadel_addons_dir,
        scan_hero_models,
        parse_csdk_path
    )
except ImportError:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    sys.path.insert(0, script_dir)
    from vmdl_upgrader import (
        process_vmdl_file,
        derive_default_paths,
        detect_hero_from_path,
        HERO_DATABASE,
        DEFAULT_CSWIN_DIR,
        DEFAULT_CITADEL_ADDONS_DIR,
        load_config,
        save_config,
        update_config,
        extract_citadel_addons_dir,
        scan_hero_models,
        parse_csdk_path
    )


# Clean, high-contrast dark color palette (No emojis, legible fonts)
THEME = {
    "bg": "#1e1e1e",
    "card_bg": "#252526",
    "card_border": "#3c3c3c",
    "text_primary": "#ffffff",
    "text_secondary": "#cccccc",
    "text_muted": "#888888",
    "entry_bg": "#181818",
    "entry_fg": "#ffffff",
    "btn_primary_bg": "#007acc",
    "btn_primary_fg": "#ffffff",
    "btn_primary_active": "#0098ff",
    "btn_sec_bg": "#333333",
    "btn_sec_fg": "#cccccc",
    "btn_sec_active": "#444444",
    "status_ok": "#4ec9b0",
    "status_warn": "#ce9178",
    "status_err": "#f44747",
    "status_info": "#9cdcfe",
    "log_bg": "#141414",
    "log_fg": "#d4d4d4"
}


class CollapsibleFrame(ttk.Frame):
    """Collapsible container for advanced options."""
    def __init__(self, parent, title="Advanced Options", is_expanded=False, *args, **kwargs):
        super().__init__(parent, *args, **kwargs)
        self.is_expanded = is_expanded
        self.title_text = title

        self.header_frame = ttk.Frame(self, style="Card.TFrame")
        self.header_frame.pack(fill=tk.X, expand=True)

        self.toggle_btn = tk.Button(
            self.header_frame,
            text=f"[{'-' if self.is_expanded else '+'}]  {self.title_text}",
            command=self.toggle,
            font=("Segoe UI", 9, "bold"),
            bg=THEME["card_bg"],
            fg=THEME["text_secondary"],
            activebackground=THEME["btn_sec_bg"],
            activeforeground=THEME["text_primary"],
            relief=tk.FLAT,
            bd=0,
            anchor="w",
            padx=6,
            pady=4,
            cursor="hand2"
        )
        self.toggle_btn.pack(fill=tk.X, expand=True)

        self.content_frame = ttk.Frame(self, style="Card.TFrame", padding="8 4 8 8")
        if self.is_expanded:
            self.content_frame.pack(fill=tk.X, expand=True, pady=(2, 0))

    def toggle(self):
        self.is_expanded = not self.is_expanded
        self.toggle_btn.config(text=f"[{'-' if self.is_expanded else '+'}]  {self.title_text}")
        if self.is_expanded:
            self.content_frame.pack(fill=tk.X, expand=True, pady=(2, 0))
        else:
            self.content_frame.pack_forget()


class VMDLUpgraderModernGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("Deadlock AG2 VMDL Compiler (CSDK12)")
        self.root.geometry("860x780")
        self.root.minsize(740, 620)
        self.root.configure(bg=THEME["bg"])

        self.log_queue = queue.Queue()
        self.is_processing = False
        self.discovered_models = []

        # Load persisted settings
        self.config = load_config()

        self._setup_styles()
        self._build_ui()
        self._check_environment_status()
        self.rescan_models(log_output=False)

        # Start log polling loop
        self.root.after(100, self._process_log_queue)

    def _setup_styles(self):
        style = ttk.Style()
        style.theme_use('clam')

        style.configure(".", background=THEME["bg"], foreground=THEME["text_primary"], font=("Segoe UI", 9))
        style.configure("TFrame", background=THEME["bg"])
        style.configure("Card.TFrame", background=THEME["card_bg"])

        # Label Frames
        style.configure(
            "Card.TLabelframe",
            background=THEME["card_bg"],
            bordercolor=THEME["card_border"],
            darkcolor=THEME["card_border"],
            lightcolor=THEME["card_border"],
            relief="solid"
        )
        style.configure(
            "Card.TLabelframe.Label",
            font=("Segoe UI", 10, "bold"),
            foreground=THEME["text_primary"],
            background=THEME["card_bg"]
        )

        # Labels
        style.configure("TLabel", background=THEME["bg"], foreground=THEME["text_primary"])
        style.configure("Card.TLabel", background=THEME["card_bg"], foreground=THEME["text_secondary"])
        style.configure("Dim.TLabel", background=THEME["card_bg"], foreground=THEME["text_muted"], font=("Segoe UI", 8))
        style.configure("Header.TLabel", background=THEME["bg"], foreground=THEME["text_primary"], font=("Segoe UI", 13, "bold"))
        style.configure("SubHeader.TLabel", background=THEME["bg"], foreground=THEME["text_muted"], font=("Segoe UI", 9))

        # Checkbuttons
        style.configure(
            "Card.TCheckbutton",
            background=THEME["card_bg"],
            foreground=THEME["text_secondary"],
            font=("Segoe UI", 9),
            focuscolor=THEME["card_bg"]
        )
        style.map(
            "Card.TCheckbutton",
            background=[("active", THEME["card_bg"])],
            foreground=[("active", THEME["text_primary"])]
        )

        # Progressbar
        style.configure(
            "Modern.Horizontal.TProgressbar",
            troughcolor=THEME["entry_bg"],
            background=THEME["btn_primary_bg"],
            bordercolor=THEME["card_border"],
            lightcolor=THEME["btn_primary_bg"],
            darkcolor=THEME["btn_primary_bg"]
        )

        # Combobox styling
        style.configure(
            "TCombobox",
            fieldbackground=THEME["entry_bg"],
            background=THEME["btn_sec_bg"],
            foreground=THEME["entry_fg"],
            darkcolor=THEME["card_border"],
            lightcolor=THEME["card_border"],
            bordercolor=THEME["card_border"],
            arrowcolor=THEME["text_primary"],
            font=("Segoe UI", 9)
        )
        style.map(
            "TCombobox",
            fieldbackground=[("readonly", THEME["entry_bg"])],
            foreground=[("readonly", THEME["entry_fg"])],
            selectbackground=[("readonly", THEME["btn_primary_bg"])],
            selectforeground=[("readonly", THEME["text_primary"])]
        )

    def _build_ui(self):
        main_container = ttk.Frame(self.root, padding="12")
        main_container.pack(fill=tk.BOTH, expand=True)

        # -------------------------------------------------------------
        # 1. HEADER SECTION
        # -------------------------------------------------------------
        header_frame = ttk.Frame(main_container)
        header_frame.pack(fill=tk.X, pady=(0, 8))

        ttk.Label(header_frame, text="CSDK12 AnimGraph2 VMDL Pipeline & Compiler", style="Header.TLabel").pack(anchor="w")
        ttk.Label(
            header_frame,
            text="Injects AnimGraph2 / NmSkeleton nodes, compiles with CSWin64 and deploys .vmdl_c to CSDK12 game directory.",
            style="SubHeader.TLabel"
        ).pack(anchor="w", pady=(1, 0))

        # -------------------------------------------------------------
        # 2. ENVIRONMENT PATHS
        # -------------------------------------------------------------
        env_frame = ttk.LabelFrame(main_container, text=" Environment Paths ", style="Card.TLabelframe", padding="8")
        env_frame.pack(fill=tk.X, pady=(0, 6))

        # CSWin64 Row
        ttk.Label(env_frame, text="CSWin64 Path:", style="Card.TLabel").grid(row=0, column=0, sticky="w", pady=3)
        self.cswin_dir_var = tk.StringVar(value=self.config.get("cswin_dir", DEFAULT_CSWIN_DIR))
        self.cswin_dir_var.trace_add("write", self._on_cswin_path_changed)
        
        self.cswin_entry = self._create_dark_entry(env_frame, self.cswin_dir_var)
        self.cswin_entry.grid(row=0, column=1, sticky="ew", padx=6, pady=3)

        self._create_flat_btn(env_frame, "Browse...", self.browse_cswin_folder).grid(row=0, column=2, sticky="e", pady=3)
        
        self.lbl_cswin_status = ttk.Label(env_frame, text="Checking CSWin64...", style="Dim.TLabel")
        self.lbl_cswin_status.grid(row=1, column=1, sticky="w", padx=6, pady=(0, 3))

        # Citadel Addons Row
        ttk.Label(env_frame, text="Citadel Addons:", style="Card.TLabel").grid(row=2, column=0, sticky="w", pady=3)
        self.citadel_addons_var = tk.StringVar(value=self.config.get("citadel_addons_dir", DEFAULT_CITADEL_ADDONS_DIR))
        self.citadel_addons_var.trace_add("write", self._on_citadel_path_changed)

        self.citadel_entry = self._create_dark_entry(env_frame, self.citadel_addons_var)
        self.citadel_entry.grid(row=2, column=1, sticky="ew", padx=6, pady=3)

        self._create_flat_btn(env_frame, "Browse...", self.browse_citadel_folder).grid(row=2, column=2, sticky="e", pady=3)

        self.lbl_citadel_status = ttk.Label(env_frame, text="Checking Citadel Addons...", style="Dim.TLabel")
        self.lbl_citadel_status.grid(row=3, column=1, sticky="w", padx=6, pady=(0, 3))

        env_frame.columnconfigure(1, weight=1)

        # -------------------------------------------------------------
        # 3. TARGET MODEL SELECTION (Auto-Discovery + Manual)
        # -------------------------------------------------------------
        target_frame = ttk.LabelFrame(main_container, text=" Model Selection ", style="Card.TLabelframe", padding="8")
        target_frame.pack(fill=tk.X, pady=(0, 6))

        # Row 0: Discovered Hero Models Combobox (Strictly known heroes from database)
        ttk.Label(target_frame, text="Hero Models:", style="Card.TLabel").grid(row=0, column=0, sticky="w", pady=3)

        self.discovered_var = tk.StringVar(value="(Scanning for hero models...)")
        self.discovered_combo = ttk.Combobox(
            target_frame,
            textvariable=self.discovered_var,
            state="readonly",
            font=("Segoe UI", 9)
        )
        self.discovered_combo.grid(row=0, column=1, sticky="ew", padx=6, pady=3)
        self.discovered_combo.bind("<<ComboboxSelected>>", self.on_discovered_selected)

        box_discover_btns = ttk.Frame(target_frame, style="Card.TFrame")
        box_discover_btns.grid(row=0, column=2, sticky="e", pady=3)

        self._create_flat_btn(box_discover_btns, "Rescan", self.rescan_models).pack(side=tk.LEFT, padx=1)

        self.lbl_discovered_count = ttk.Label(target_frame, text="Scanning...", style="Dim.TLabel")
        self.lbl_discovered_count.grid(row=1, column=1, sticky="w", padx=6, pady=(0, 3))

        # Row 2: Direct File / Path Entry
        ttk.Label(target_frame, text="Target File / Path:", style="Card.TLabel").grid(row=2, column=0, sticky="w", pady=3)
        self.path_var = tk.StringVar(value=self.config.get("last_target_path", ""))
        self.path_var.trace_add("write", self._on_target_path_changed)

        self.path_entry = self._create_dark_entry(target_frame, self.path_var)
        self.path_entry.grid(row=2, column=1, sticky="ew", padx=6, pady=3)

        btn_target_box = ttk.Frame(target_frame, style="Card.TFrame")
        btn_target_box.grid(row=2, column=2, sticky="e", pady=3)

        self._create_flat_btn(btn_target_box, "Browse...", self.browse_file).pack(side=tk.LEFT, padx=1)

        # Row 3: Hero Preset
        ttk.Label(target_frame, text="Hero Preset:", style="Card.TLabel").grid(row=3, column=0, sticky="w", pady=3)
        
        preset_names = ["(Auto-Detect Hero Paths)"] + sorted(list(HERO_DATABASE.keys()))
        self.preset_var = tk.StringVar(value=preset_names[0])
        self.preset_combo = ttk.Combobox(
            target_frame,
            textvariable=self.preset_var,
            values=preset_names,
            state="readonly",
            font=("Segoe UI", 9)
        )
        self.preset_combo.grid(row=3, column=1, sticky="w", padx=6, pady=3)
        self.preset_combo.bind("<<ComboboxSelected>>", self.on_preset_selected)

        self.lbl_hero_detect = ttk.Label(target_frame, text="Auto-Detection: Ready", style="Dim.TLabel")
        self.lbl_hero_detect.grid(row=3, column=2, sticky="w", padx=4)

        target_frame.columnconfigure(1, weight=1)

        # -------------------------------------------------------------
        # 4. ACTION BAR & PROGRESS BAR
        # -------------------------------------------------------------
        action_frame = ttk.Frame(main_container)
        action_frame.pack(fill=tk.X, pady=(0, 6))

        self.btn_run = tk.Button(
            action_frame,
            text="Compile & Deploy VMDL",
            command=self.start_pipeline_thread,
            font=("Segoe UI", 10, "bold"),
            bg=THEME["btn_primary_bg"],
            fg=THEME["btn_primary_fg"],
            activebackground=THEME["btn_primary_active"],
            activeforeground="#ffffff",
            relief=tk.FLAT,
            bd=0,
            padx=12,
            pady=7,
            cursor="hand2"
        )
        self.btn_run.pack(fill=tk.X, pady=(0, 4))

        self.progress_bar = ttk.Progressbar(action_frame, style="Modern.Horizontal.TProgressbar", mode="determinate")
        self.progress_bar.pack(fill=tk.X, pady=(0, 4))

        # Toolbar Row
        toolbar_frame = ttk.Frame(action_frame)
        toolbar_frame.pack(fill=tk.X)

        self._create_small_btn(toolbar_frame, "Clear Log", self.clear_log).pack(side=tk.RIGHT, padx=(4, 0))
        self._create_small_btn(toolbar_frame, "Copy Log", self.copy_log).pack(side=tk.RIGHT, padx=(4, 0))

        # -------------------------------------------------------------
        # 5. ADVANCED OPTIONS (Collapsible)
        # -------------------------------------------------------------
        self.collapsible_options = CollapsibleFrame(main_container, title="Compiler Options & Node Overrides", is_expanded=False)
        self.collapsible_options.pack(fill=tk.X, pady=(0, 6))

        opts = self.collapsible_options.content_frame

        # Checkboxes
        self.chk_compile = tk.BooleanVar(value=self.config.get("chk_compile", True))
        self.chk_revert = tk.BooleanVar(value=self.config.get("chk_revert", True))
        self.chk_header = tk.BooleanVar(value=self.config.get("chk_header", True))
        self.chk_skel = tk.BooleanVar(value=self.config.get("chk_skel", True))
        self.chk_graph = tk.BooleanVar(value=self.config.get("chk_graph", True))
        self.chk_ui_graph = tk.BooleanVar(value=self.config.get("chk_ui_graph", True))
        self.chk_backup = tk.BooleanVar(value=self.config.get("chk_backup", True))

        for var in [self.chk_compile, self.chk_revert, self.chk_header, self.chk_skel, self.chk_graph, self.chk_ui_graph, self.chk_backup]:
            var.trace_add("write", self._save_options_config)

        row_c = 0
        ttk.Checkbutton(opts, text="Compile via CSWin64 resourcecompiler and deploy .vmdl_c to game directory", variable=self.chk_compile, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1
        ttk.Checkbutton(opts, text="Auto-revert .vmdl file to pre-upgrade state after compilation (ModelDoc compatible)", variable=self.chk_revert, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1
        ttk.Checkbutton(opts, text="Upgrade format header to modeldoc41 during compilation", variable=self.chk_header, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1
        ttk.Checkbutton(opts, text="Inject NmSkeletonList node (.vnmskel)", variable=self.chk_skel, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1
        ttk.Checkbutton(opts, text="Inject DefaultAnimGraph2 node (.vnmgraph)", variable=self.chk_graph, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1
        ttk.Checkbutton(opts, text="Include UI AnimGraph2 node (ui)", variable=self.chk_ui_graph, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1
        ttk.Checkbutton(opts, text="Create .bak backup before modifying .vmdl", variable=self.chk_backup, style="Card.TCheckbutton").grid(row=row_c, column=0, columnspan=3, sticky="w", pady=2)
        row_c += 1

        # Custom Overrides
        ttk.Label(opts, text="Skeleton Ref (.vnmskel):", style="Card.TLabel").grid(row=row_c, column=0, sticky="w", pady=3)
        self.skel_var = tk.StringVar()
        self._create_dark_entry(opts, self.skel_var).grid(row=row_c, column=1, columnspan=2, sticky="ew", padx=6, pady=3)
        row_c += 1

        ttk.Label(opts, text="Default AnimGraph Ref:", style="Card.TLabel").grid(row=row_c, column=0, sticky="w", pady=3)
        self.graph_var = tk.StringVar()
        self._create_dark_entry(opts, self.graph_var).grid(row=row_c, column=1, columnspan=2, sticky="ew", padx=6, pady=3)
        row_c += 1

        ttk.Label(opts, text="UI AnimGraph Ref (ui):", style="Card.TLabel").grid(row=row_c, column=0, sticky="w", pady=3)
        self.ui_graph_var = tk.StringVar()
        self._create_dark_entry(opts, self.ui_graph_var).grid(row=row_c, column=1, columnspan=2, sticky="ew", padx=6, pady=3)

        opts.columnconfigure(1, weight=1)

        # -------------------------------------------------------------
        # 6. PROGRESS LOG TERMINAL
        # -------------------------------------------------------------
        log_frame = ttk.LabelFrame(main_container, text=" Output Log ", style="Card.TLabelframe", padding="6")
        log_frame.pack(fill=tk.BOTH, expand=True)

        self.log_text = ScrolledText(
            log_frame,
            wrap=tk.WORD,
            font=("Consolas", 9),
            bg=THEME["log_bg"],
            fg=THEME["log_fg"],
            insertbackground="#ffffff",
            relief=tk.FLAT,
            bd=0,
            padx=6,
            pady=6
        )
        self.log_text.pack(fill=tk.BOTH, expand=True)

        # Log Tags
        self.log_text.tag_config("success", foreground=THEME["status_ok"], font=("Consolas", 9, "bold"))
        self.log_text.tag_config("error", foreground=THEME["status_err"], font=("Consolas", 9, "bold"))
        self.log_text.tag_config("warning", foreground=THEME["status_warn"])
        self.log_text.tag_config("info", foreground=THEME["status_info"])
        self.log_text.tag_config("dim", foreground=THEME["text_muted"])
        self.log_text.tag_config("bold", font=("Consolas", 9, "bold"))

        self.log("CSDK12 AnimGraph2 Compiler Ready.", tag="info")
        self.log("Select a hero model or browse for a file to begin.", tag="dim")

    # -----------------------------------------------------------------
    # HELPER UI WIDGET CREATORS
    # -----------------------------------------------------------------
    def _create_flat_btn(self, parent, text, cmd):
        return tk.Button(
            parent,
            text=text,
            command=cmd,
            font=("Segoe UI", 9),
            bg=THEME["btn_sec_bg"],
            fg=THEME["btn_sec_fg"],
            activebackground=THEME["btn_sec_active"],
            activeforeground=THEME["text_primary"],
            relief=tk.FLAT,
            bd=0,
            padx=8,
            pady=3,
            cursor="hand2"
        )

    def _create_small_btn(self, parent, text, cmd):
        return tk.Button(
            parent,
            text=text,
            command=cmd,
            font=("Segoe UI", 8),
            bg=THEME["btn_sec_bg"],
            fg=THEME["btn_sec_fg"],
            activebackground=THEME["btn_sec_active"],
            activeforeground=THEME["text_primary"],
            relief=tk.FLAT,
            bd=0,
            padx=7,
            pady=2,
            cursor="hand2"
        )

    def _create_dark_entry(self, parent, textvar):
        return tk.Entry(
            parent,
            textvariable=textvar,
            bg=THEME["entry_bg"],
            fg=THEME["entry_fg"],
            insertbackground="#ffffff",
            relief=tk.SOLID,
            bd=1,
            highlightthickness=0,
            font=("Segoe UI", 9)
        )

    # -----------------------------------------------------------------
    # ENVIRONMENT & HERO DISCOVERY MANAGEMENT
    # -----------------------------------------------------------------
    def rescan_models(self, log_output=True):
        """Scans citadel_addons directory for hero models matching database."""
        search_dir = self.citadel_addons_var.get().strip()
        if not search_dir or not os.path.exists(search_dir):
            target_path = self.path_var.get().strip()
            if target_path and os.path.isdir(target_path):
                search_dir = target_path

        if not search_dir or not os.path.exists(search_dir):
            self.discovered_models = []
            self.discovered_combo["values"] = ["(Set Citadel Addons folder to discover hero models)"]
            self.discovered_var.set("(Set Citadel Addons folder to discover hero models)")
            self.lbl_discovered_count.config(text="No Addons folder selected", foreground=THEME["text_muted"])
            return

        models = scan_hero_models(search_dir)
        self.discovered_models = models

        if models:
            display_values = [m["display"] for m in models]
            self.discovered_combo["values"] = display_values
            self.lbl_discovered_count.config(
                text=f"Found {len(models)} matching hero model(s)",
                foreground=THEME["status_ok"]
            )
            
            # Check if current path matches any discovered model
            current_path = os.path.normpath(self.path_var.get().strip()) if self.path_var.get().strip() else ""
            matched_idx = -1
            for idx, m in enumerate(models):
                if os.path.normpath(m["full_path"]) == current_path:
                    matched_idx = idx
                    break

            if matched_idx != -1:
                self.discovered_var.set(display_values[matched_idx])
            else:
                self.discovered_var.set(f"Select hero model ({len(models)} available)...")

            if log_output:
                self.log(f"Discovered {len(models)} hero model(s) in: {search_dir}", tag="info")
        else:
            self.discovered_combo["values"] = ["(No matching hero models found in heroes_wip / heroes_staging)"]
            self.discovered_var.set("(No matching hero models found in heroes_wip / heroes_staging)")
            self.lbl_discovered_count.config(
                text="0 matching hero models found",
                foreground=THEME["status_warn"]
            )
            if log_output:
                self.log(f"Scanned {search_dir}: No known hero models found.", tag="warning")

    def on_discovered_selected(self, event=None):
        selected_text = self.discovered_var.get().strip()
        for m in self.discovered_models:
            if selected_text == m["display"]:
                self.path_var.set(m["full_path"])
                self.log(f"Selected hero model: {m['display']}", tag="info")
                return

    def _check_environment_status(self):
        # 1. Check CSWin64
        cswin_dir = self.cswin_dir_var.get().strip()
        rc1 = os.path.join(cswin_dir, "game", "bin", "win64", "resourcecompiler.exe")
        rc2 = os.path.join(cswin_dir, "bin", "win64", "resourcecompiler.exe")

        if os.path.exists(rc1) or os.path.exists(rc2):
            self.lbl_cswin_status.config(text="Ready (resourcecompiler.exe found)", foreground=THEME["status_ok"])
        elif not cswin_dir:
            self.lbl_cswin_status.config(text="CSWin64 path not configured", foreground=THEME["status_err"])
        else:
            self.lbl_cswin_status.config(text="resourcecompiler.exe not found in this path", foreground=THEME["status_err"])

        # 2. Check Citadel Addons
        citadel_dir = self.citadel_addons_var.get().strip()
        if citadel_dir and os.path.exists(citadel_dir):
            self.lbl_citadel_status.config(text="Connected to Citadel Addons folder", foreground=THEME["status_ok"])
        elif not citadel_dir:
            self.lbl_citadel_status.config(text="Citadel Addons path not set (Optional)", foreground=THEME["text_muted"])
        else:
            self.lbl_citadel_status.config(text="Specified directory does not exist", foreground=THEME["status_warn"])

    def _on_cswin_path_changed(self, *args):
        path = self.cswin_dir_var.get().strip()
        self._check_environment_status()
        update_config(cswin_dir=path)

    def _on_citadel_path_changed(self, *args):
        path = self.citadel_addons_var.get().strip()
        self._check_environment_status()
        update_config(citadel_addons_dir=path)
        self.rescan_models(log_output=False)

    def _save_options_config(self, *args):
        update_config(
            chk_compile=self.chk_compile.get(),
            chk_revert=self.chk_revert.get(),
            chk_header=self.chk_header.get(),
            chk_skel=self.chk_skel.get(),
            chk_graph=self.chk_graph.get(),
            chk_ui_graph=self.chk_ui_graph.get(),
            chk_backup=self.chk_backup.get()
        )

    def _get_browse_initial_dir(self):
        citadel_dir = self.citadel_addons_var.get().strip()
        if citadel_dir and os.path.exists(citadel_dir):
            return citadel_dir
        last_path = self.path_var.get().strip()
        if last_path and os.path.exists(last_path):
            return os.path.dirname(last_path) if os.path.isfile(last_path) else last_path
        return os.getcwd()

    def browse_cswin_folder(self):
        init_dir = self.cswin_dir_var.get().strip()
        if not init_dir or not os.path.exists(init_dir):
            init_dir = None
        folder = filedialog.askdirectory(title="Select CSWin64 Installation Folder", initialdir=init_dir)
        if folder:
            self.cswin_dir_var.set(os.path.normpath(folder))

    def browse_citadel_folder(self):
        init_dir = self.citadel_addons_var.get().strip()
        if not init_dir or not os.path.exists(init_dir):
            init_dir = None
        folder = filedialog.askdirectory(title="Select CSDK12 citadel_addons or content Folder", initialdir=init_dir)
        if folder:
            norm = os.path.normpath(folder)
            self.citadel_addons_var.set(norm)
            self.rescan_models(log_output=True)

    def browse_file(self):
        filename = filedialog.askopenfilename(
            title="Select VMDL Model File",
            initialdir=self._get_browse_initial_dir(),
            filetypes=[("VMDL Model Files", "*.vmdl"), ("All Files", "*.*")]
        )
        if filename:
            norm_path = os.path.normpath(filename)
            self.path_var.set(norm_path)
            self._auto_detect_and_set_citadel_dir(norm_path)

    def browse_folder(self):
        folder = filedialog.askdirectory(
            title="Select Addon or Content Folder",
            initialdir=self._get_browse_initial_dir()
        )
        if folder:
            norm_path = os.path.normpath(folder)
            self.path_var.set(norm_path)
            self._auto_detect_and_set_citadel_dir(norm_path)
            self.rescan_models(log_output=True)

    def _auto_detect_and_set_citadel_dir(self, filepath):
        detected = extract_citadel_addons_dir(filepath)
        current = self.citadel_addons_var.get().strip()
        if detected and (not current or not os.path.exists(current)):
            self.citadel_addons_var.set(detected)
            self.log(f"Auto-detected Citadel Addons folder: {detected}", tag="info")
            self.rescan_models(log_output=False)

    def _on_target_path_changed(self, *args):
        path = self.path_var.get().strip()
        update_config(last_target_path=path)

        # Update discovered combobox matching item if applicable
        current_norm = os.path.normpath(path) if path else ""
        for idx, m in enumerate(self.discovered_models):
            if os.path.normpath(m["full_path"]) == current_norm:
                self.discovered_combo.current(idx)
                break

        if os.path.isfile(path) and path.lower().endswith('.vmdl'):
            skel, graph, ui_graph = derive_default_paths(path)
            self.skel_var.set(skel)
            self.graph_var.set(graph)
            self.ui_graph_var.set(ui_graph)

            detected_hero = detect_hero_from_path(path)

            if detected_hero:
                self.lbl_hero_detect.config(text=f"Detected: {detected_hero}", foreground=THEME["status_ok"])
                self.preset_var.set(detected_hero)
            else:
                self.lbl_hero_detect.config(text="Custom / Unknown Hero", foreground=THEME["status_warn"])

    def on_preset_selected(self, event):
        preset = self.preset_var.get()
        if preset in HERO_DATABASE:
            info = HERO_DATABASE[preset]
            self.skel_var.set(info["skel"])
            self.graph_var.set(info["graph"])
            self.ui_graph_var.set(info["ui_graph"])
            self.lbl_hero_detect.config(text=f"Preset: {preset}", foreground=THEME["status_info"])
            self.log(f"Applied hero preset: '{preset}'", tag="info")

    # -----------------------------------------------------------------
    # LOGGING SYSTEM
    # -----------------------------------------------------------------
    def log(self, msg, tag=None):
        self.log_queue.put((msg, tag))

    def _process_log_queue(self):
        while not self.log_queue.empty():
            msg, tag = self.log_queue.get_nowait()
            self._append_log_line(msg, tag)
        self.root.after(80, self._process_log_queue)

    def _append_log_line(self, msg, tag=None):
        if not tag:
            if "[SUCCESS]" in msg:
                tag = "success"
            elif "[FAILED]" in msg or "[ERROR]" in msg or "[EXCEPTION]" in msg:
                tag = "error"
            elif "[WARNING]" in msg:
                tag = "warning"
            else:
                tag = None

        if tag:
            self.log_text.insert(tk.END, msg + "\n", tag)
        else:
            self.log_text.insert(tk.END, msg + "\n")

        self.log_text.see(tk.END)

    def clear_log(self):
        self.log_text.delete("1.0", tk.END)

    def copy_log(self):
        content = self.log_text.get("1.0", tk.END).strip()
        if content:
            self.root.clipboard_clear()
            self.root.clipboard_append(content)
            messagebox.showinfo("Clipboard", "Log copied to clipboard.")


    # -----------------------------------------------------------------
    # PIPELINE EXECUTION (THREADED / NON-BLOCKING)
    # -----------------------------------------------------------------
    def start_pipeline_thread(self):
        if self.is_processing:
            return

        path = self.path_var.get().strip()
        cswin_dir = self.cswin_dir_var.get().strip()
        citadel_dir = self.citadel_addons_var.get().strip()

        if not path:
            messagebox.showwarning("No Target Selected", "Please select a .vmdl file or directory first.")
            return

        if not os.path.exists(path):
            messagebox.showerror("Path Error", f"Target path does not exist:\n{path}")
            return

        if self.chk_compile.get() and not os.path.exists(cswin_dir):
            messagebox.showerror("CSWin64 Path Error", f"CSWin64 directory does not exist:\n{cswin_dir}")
            return

        targets = []
        if os.path.isfile(path):
            if path.lower().endswith('.vmdl'):
                targets.append(path)
            else:
                messagebox.showerror("Invalid File", "Target file must be a .vmdl model file.")
                return
        elif os.path.isdir(path):
            for root, _, files in os.walk(path):
                for file in files:
                    if file.lower().endswith('.vmdl'):
                        targets.append(os.path.join(root, file))

        if not targets:
            messagebox.showinfo("No Files Found", "No .vmdl files were found in the selected target.")
            return

        # Disable UI and start thread
        self.is_processing = True
        self.btn_run.config(text="Compiling & Deploying...", state=tk.DISABLED, bg=THEME["btn_sec_bg"], fg=THEME["text_muted"])
        self.progress_bar["value"] = 0
        self.progress_bar["maximum"] = len(targets)

        thread = threading.Thread(
            target=self._worker_execute,
            args=(targets, cswin_dir, citadel_dir),
            daemon=True
        )
        thread.start()

    def _worker_execute(self, targets, cswin_dir, citadel_dir):
        total = len(targets)
        self.log(f"\n--- Starting Pipeline for {total} file(s) ---", tag="info")
        self.log(f"CSWin64 Path: {cswin_dir}", tag="dim")
        if citadel_dir:
            self.log(f"Citadel Addons: {citadel_dir}", tag="dim")

        updated_count = 0
        error_count = 0

        skel_custom = self.skel_var.get().strip() or None
        graph_custom = self.graph_var.get().strip() or None
        ui_graph_custom = self.ui_graph_var.get().strip() or None

        chk_backup = self.chk_backup.get()
        chk_skel = self.chk_skel.get()
        chk_graph = self.chk_graph.get()
        chk_ui_graph = self.chk_ui_graph.get()
        chk_header = self.chk_header.get()
        chk_compile = self.chk_compile.get()
        chk_revert = self.chk_revert.get()

        for idx, target in enumerate(targets, 1):
            try:
                success, msg = process_vmdl_file(
                    target,
                    skel_path=skel_custom,
                    graph_path=graph_custom,
                    ui_graph_path=ui_graph_custom,
                    create_backup=chk_backup,
                    add_skel=chk_skel,
                    add_graph=chk_graph,
                    add_ui_graph=chk_ui_graph,
                    upgrade_header=chk_header,
                    compile_cswin=chk_compile,
                    revert_vmdl=chk_revert,
                    cswin_dir=cswin_dir,
                    citadel_addons_dir=citadel_dir
                )
                if success:
                    updated_count += 1
                    self.log(f"[{idx}/{total}] [SUCCESS] {os.path.basename(target)}\n        {msg}")
                else:
                    error_count += 1
                    self.log(f"[{idx}/{total}] [FAILED] {os.path.basename(target)}\n        {msg}")
            except Exception as e:
                error_count += 1
                self.log(f"[{idx}/{total}] [EXCEPTION] {os.path.basename(target)}: {str(e)}", tag="error")

            self.root.after(0, self._step_progress, idx)

        summary = f"Pipeline Finished. Processed: {updated_count}, Errors: {error_count}"
        self.log(f"--- {summary} ---\n", tag="info")

        self.root.after(0, self._finish_pipeline, summary, error_count)

    def _step_progress(self, current_val):
        self.progress_bar["value"] = current_val

    def _finish_pipeline(self, summary, error_count):
        self.is_processing = False
        self.btn_run.config(
            text="Compile & Deploy VMDL",
            state=tk.NORMAL,
            bg=THEME["btn_primary_bg"],
            fg=THEME["btn_primary_fg"]
        )
        if error_count == 0:
            messagebox.showinfo("Pipeline Complete", summary)
        else:
            messagebox.showwarning("Pipeline Completed with Warnings", summary)


def main():
    root = tk.Tk()
    app = VMDLUpgraderModernGUI(root)
    root.mainloop()


if __name__ == '__main__':
    main()
