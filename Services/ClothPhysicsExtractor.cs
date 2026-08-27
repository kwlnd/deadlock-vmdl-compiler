using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;

namespace DeadlockVmdlCompiler.Services;

public static class ClothPhysicsExtractor
{
    private static readonly string[] CommonSteamDeadlockPaths = new[]
    {
        @"D:\Games\Steam\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"C:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"E:\Games\Steam\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk"
    };

    public static string? FindDeadlockVpkPath(string? citadelAddonsDir = null)
    {
        if (!string.IsNullOrWhiteSpace(citadelAddonsDir) && Directory.Exists(citadelAddonsDir))
        {
            var parent = Directory.GetParent(citadelAddonsDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var candidate = Path.Combine(parent, "citadel", "pak01_dir.vpk");
                if (File.Exists(candidate)) return candidate;

                var candidate2 = Path.Combine(parent, "pak01_dir.vpk");
                if (File.Exists(candidate2)) return candidate2;
            }

            var rootParent = Directory.GetParent(citadelAddonsDir)?.Parent?.FullName;
            if (!string.IsNullOrEmpty(rootParent))
            {
                var candidate = Path.Combine(rootParent, "game", "citadel", "pak01_dir.vpk");
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var p in CommonSteamDeadlockPaths)
        {
            if (File.Exists(p)) return p;
        }

        try
        {
            var loc = DeadlockLocator.DetectDeadlockInstallation();
            if (loc.IsValid && File.Exists(loc.Pak01VpkPath))
                return loc.Pak01VpkPath;
        }
        catch { }

        return null;
    }

    public static string ResolveVpkSubpath(string vmdlPath, string? citadelAddonsDir = null)
    {
        var clean = vmdlPath.Replace('\\', '/');

        var m = Regex.Match(clean, @"(models/heroes[^/]+/[^/]+/[^/]+\.vmdl)$", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[1].Value + "_c";

        var mGeneral = Regex.Match(clean, @"(models/.+\.vmdl)$", RegexOptions.IgnoreCase);
        if (mGeneral.Success)
            return mGeneral.Groups[1].Value + "_c";

        var (_, _, subpath) = VmdlPipeline.ParseCsdkPath(vmdlPath, citadelAddonsDir);
        if (!string.IsNullOrEmpty(subpath))
        {
            if (subpath.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                return subpath + "_c";
            return subpath;
        }

        return Path.GetFileName(clean) + "_c";
    }

    public static List<string> ExtractExactSkeletonBones(string vmdlContent)
    {
        var matches = Regex.Matches(vmdlContent, @"_class\s*=\s*""Bone""\s*name\s*=\s*""([^""]+)""");
        return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    public class BoneChain
    {
        public string ChainName { get; set; } = string.Empty;
        public List<string> Bones { get; set; } = new();
    }

    public static List<BoneChain> DetectSkeletonChains(List<string> skeletonBones)
    {
        var chains = new List<BoneChain>();
        var prefixKeywords = new[] { 
            "hair", "braid", "tail", "bell", "skirt", "cape", "dress", "ribbon", "rope", "chain", 
            "sleeve", "ear", "fur", "cuff", "handcuff", "shackle", "bola", "bolas", 
            "tassel", "pouch", "scarf", "cloak", "feather", "wing", "charm", "string" 
        };

        var indexedGroups = new Dictionary<string, List<(int Index, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var lrGroups = new Dictionary<string, List<(int Index, string Name)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var bone in skeletonBones)
        {
            var mLR = Regex.Match(bone, @"^(.*?)_([0-9]+)_([rlRL])$");
            if (mLR.Success)
            {
                var groupKey = mLR.Groups[1].Value + "_" + mLR.Groups[3].Value.ToLowerInvariant();
                int idx = int.Parse(mLR.Groups[2].Value);
                if (!lrGroups.ContainsKey(groupKey)) lrGroups[groupKey] = new();
                lrGroups[groupKey].Add((idx, bone));
                continue;
            }

            var mIndex = Regex.Match(bone, @"^(.*?)_([0-9]+)$");
            if (mIndex.Success) {
                var prefix = mIndex.Groups[1].Value;
                int idx = int.Parse(mIndex.Groups[2].Value);
                if (!indexedGroups.ContainsKey(prefix)) indexedGroups[prefix] = new();
                indexedGroups[prefix].Add((idx, bone));
                continue;
            }
        }

        // Process LR groups (e.g. hair_front_0_r ... hair_front_3_r -> hair_front_end_r)
        foreach (var kvp in lrGroups)
        {
            var groupKey = kvp.Key;
            var list = kvp.Value.OrderBy(x => x.Index).ToList();
            if (prefixKeywords.Any(k => groupKey.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                var chain = new BoneChain { ChainName = "chain_" + groupKey.ToLowerInvariant() };
                chain.Bones.AddRange(list.Select(x => x.Name));

                var basePrefix = groupKey[..^2];
                var side = groupKey[^1..];
                var endCandidate = $"{basePrefix}_end_{side}";
                var endBone = skeletonBones.FirstOrDefault(b => b.Equals(endCandidate, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(endBone)) chain.Bones.Add(endBone);

                if (chain.Bones.Count >= 2)
                    chains.Add(chain);
            }
        }

        // Process Indexed groups (e.g. tail_0 ... tail_5 -> tail_end, bolas_0..end, cuffs_0..end, jacket_sleeve_0..end)
        foreach (var kvp in indexedGroups)
        {
            var prefix = kvp.Key;
            var list = kvp.Value.OrderBy(x => x.Index).ToList();
            if (prefixKeywords.Any(k => prefix.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                var chain = new BoneChain { ChainName = "chain_" + prefix.ToLowerInvariant() };
                chain.Bones.AddRange(list.Select(x => x.Name));

                var endBone = skeletonBones.FirstOrDefault(b => b.Equals(prefix + "_end", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(endBone)) chain.Bones.Add(endBone);

                if (chain.Bones.Count >= 2)
                    chains.Add(chain);
            }
        }

        return chains;
    }

    public static string GenerateClothChainKv3(BoneChain chain)
    {
        var chainSb = new StringBuilder();
        chainSb.AppendLine("{\n\t\t\t\t\t_class = \"ClothChain\"\n\t\t\t\t\tname = \"" + chain.ChainName + "\"\n\t\t\t\t\troot_bone = \"\"\n\t\t\t\t\tchain = \n\t\t\t\t\t{\n\t\t\t\t\t\tjoints = \n\t\t\t\t\t\t[");
        
        bool isRigidProp = chain.ChainName.Contains("cuff") || chain.ChainName.Contains("bola") || chain.ChainName.Contains("shackle") || chain.ChainName.Contains("bottle") || chain.ChainName.Contains("flask");

        for (int j = 0; j < chain.Bones.Count; j++)
        {
            var bName = chain.Bones[j];
            bool isRoot = (j == 0);
            var parentName = isRoot ? "" : chain.Bones[j - 1];

            chainSb.AppendLine("\t\t\t\t\t\t\t{");
            chainSb.AppendLine("\t\t\t\t\t\t\t\tjoint_name = \"" + bName + "\"");
            if (!isRoot)
                chainSb.AppendLine("\t\t\t\t\t\t\t\tjoint_parent = \"" + parentName + "\"");
            chainSb.AppendLine("\t\t\t\t\t\t\t\tsimulate = " + (isRoot ? "false" : "true"));
            if (!isRoot)
            {
                float ratio = (float)j / (float)chain.Bones.Count;
                float bend = isRigidProp ? 0.65f : Math.Max(0.3f, 0.7f - ratio * 0.35f);
                float goal = isRigidProp ? 0.50f : Math.Max(0.2f, 0.6f - ratio * 0.35f);
                float mass = isRigidProp ? 2.5f : Math.Max(0.5f, 2.0f - ratio * 1.2f);
                float radius = Math.Max(0.7f, 1.5f - ratio * 0.7f);
                float grav = isRigidProp ? 3.50f : 2.50f;

                chainSb.AppendLine("\t\t\t\t\t\t\t\tstretch_spring = 1.0");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tbend_spring = " + bend.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                chainSb.AppendLine("\t\t\t\t\t\t\t\ttorsion_spring = 0.25");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tgoal_strength = " + goal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                chainSb.AppendLine("\t\t\t\t\t\t\t\tgoal_damping = 0.50");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tdrag = 0.02");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tmass = " + mass.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                chainSb.AppendLine("\t\t\t\t\t\t\t\tgravity_z = " + grav.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                chainSb.AppendLine("\t\t\t\t\t\t\t\tcollision_radius = " + radius.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                chainSb.AppendLine("\t\t\t\t\t\t\t\tstiff_hinge = 0.35");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tstiff_hinge_angle = 45.0");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tmotion_bias = 0.05");
                chainSb.AppendLine("\t\t\t\t\t\t\t\ttwist_relax = 0.0");
                chainSb.AppendLine("\t\t\t\t\t\t\t\textra_iterations = 6");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tantishrink = 1.0");
                chainSb.AppendLine("\t\t\t\t\t\t\t\tworld_collision = true");
            }
            chainSb.AppendLine("\t\t\t\t\t\t\t}" + (j < chain.Bones.Count - 1 ? "," : ""));
        }
        chainSb.AppendLine("\t\t\t\t\t\t]\n\t\t\t\t\t\tversion = 2\n\t\t\t\t\t}\n\t\t\t\t}");
        return chainSb.ToString().Trim();
    }

    public static List<string> GenerateClothShapeSpheresKv3(List<string> skeletonBones, string? heroName = null)
    {
        bool isLargeHero = !string.IsNullOrEmpty(heroName) && 
            (heroName.Contains("bull", StringComparison.OrdinalIgnoreCase) || 
             heroName.Contains("abrams", StringComparison.OrdinalIgnoreCase) || 
             heroName.Contains("bebop", StringComparison.OrdinalIgnoreCase) || 
             heroName.Contains("dynamo", StringComparison.OrdinalIgnoreCase));

        var coreBones = skeletonBones.Where(b => b.Equals("head", StringComparison.OrdinalIgnoreCase) ||
                                                 b.Equals("pelvis", StringComparison.OrdinalIgnoreCase) ||
                                                 b.Equals("spine_0", StringComparison.OrdinalIgnoreCase) ||
                                                 b.Equals("spine_2", StringComparison.OrdinalIgnoreCase) ||
                                                 b.Equals("spine_3", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        var list = new List<string>();
        foreach (var bone in coreBones)
        {
            float radius = 6.0f;
            if (bone.Equals("pelvis", StringComparison.OrdinalIgnoreCase))
                radius = isLargeHero ? 10.5f : 8.5f;
            else if (bone.StartsWith("spine", StringComparison.OrdinalIgnoreCase))
                radius = isLargeHero ? 9.5f : 7.5f;
            else if (bone.Equals("head", StringComparison.OrdinalIgnoreCase))
                radius = isLargeHero ? 7.5f : 6.0f;

            var sphere = "{\n\t\t\t\t\t_class = \"ClothShapeSphere\"\n\t\t\t\t\tname = \"" + bone + "_clothSphere\"\n\t\t\t\t\tparent_bone = \"" + bone + "\"\n\t\t\t\t\tcloth_collision_layer0 = true\n\t\t\t\t\tcloth_collision_layer1 = true\n\t\t\t\t\tcloth_collision_layer2 = true\n\t\t\t\t\tcloth_collision_layer3 = true\n\t\t\t\t\tcloth_collision_priority = 0\n\t\t\t\t\tvertex_map = \"\"\n\t\t\t\t\tinverted_collision = false\n\t\t\t\t\tplanarize = false\n\t\t\t\t\tbounciness = 0.0\n\t\t\t\t\tradius = " + radius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "\n\t\t\t\t\tcenter = [ 0.0, 0.0, 0.0 ]\n\t\t\t\t}";
            list.Add(sphere);
        }
        return list;
    }

    public static string StripExistingClothNodes(string content)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Softbody",
            "PhysicsBodyMarkupList",
            "ClothChain",
            "ClothShapeList",
            "ClothShapeSphere",
            "ClothShapeCapsule",
            "ClothShapeBox",
            "ClothProxyMeshList"
        };

        int rootIdx = content.IndexOf("rootNode", StringComparison.OrdinalIgnoreCase);
        if (rootIdx < 0) return content;

        int childrenIdx = content.IndexOf("children", rootIdx, StringComparison.OrdinalIgnoreCase);
        if (childrenIdx < 0) return content;

        int openBracket = content.IndexOf('[', childrenIdx);
        if (openBracket < 0) return content;

        int depth = 0;
        int closeBracket = -1;
        for (int i = openBracket; i < content.Length; i++)
        {
            if (content[i] == '[') depth++;
            else if (content[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    closeBracket = i;
                    break;
                }
            }
        }
        if (closeBracket < 0) return content;

        string beforeChildren = content[..(openBracket + 1)];
        string childrenContent = content.Substring(openBracket + 1, closeBracket - openBracket - 1);
        string afterChildren = content[closeBracket..];

        var sb = new StringBuilder();
        int k = 0;
        while (k < childrenContent.Length)
        {
            if (childrenContent[k] == '{')
            {
                int start = k;
                int bDepth = 0;
                int m = k;
                while (m < childrenContent.Length)
                {
                    if (childrenContent[m] == '{') bDepth++;
                    else if (childrenContent[m] == '}')
                    {
                        bDepth--;
                        if (bDepth == 0)
                        {
                            m++;
                            while (m < childrenContent.Length && (childrenContent[m] == ' ' || childrenContent[m] == '\t')) m++;
                            if (m < childrenContent.Length && childrenContent[m] == ',') m++;
                            while (m < childrenContent.Length && (childrenContent[m] == '\r' || childrenContent[m] == '\n')) m++;
                            break;
                        }
                    }
                    m++;
                }

                string block = childrenContent.Substring(start, Math.Min(m - start, childrenContent.Length - start));
                bool isTarget = false;
                foreach (var t in targets)
                {
                    if (block.Contains("_class = \"" + t + "\"") || block.Contains("_class=\"" + t + "\""))
                    {
                        isTarget = true;
                        break;
                    }
                }

                if (isTarget)
                {
                    k = m;
                    continue;
                }
            }

            sb.Append(childrenContent[k]);
            k++;
        }

        return beforeChildren + sb.ToString() + afterChildren;
    }

    public static (bool Success, string Message, int TransferredCount) TransferClothPhysics(
        string targetVmdlPath,
        string? citadelAddonsDir = null,
        string? customVpkPath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetVmdlPath) || !File.Exists(targetVmdlPath))
                return (false, $"Target file does not exist: {targetVmdlPath}", 0);

            var targetVmdlContent = File.ReadAllText(targetVmdlPath);
            var skeletonBones = ExtractExactSkeletonBones(targetVmdlContent);

            if (skeletonBones.Count == 0)
            {
                return (false, "Could not find skeleton bone definitions in target VMDL.", 0);
            }

            // 1. Detect all valid bone chains strictly from verified skeleton
            var heroName = VmdlPipeline.DetectHeroFromPath(targetVmdlPath);
            var detectedChains = DetectSkeletonChains(skeletonBones);
            var softbodyChildren = new List<string>();

            // Add colliders with hero-adapted proportions
            var shapes = GenerateClothShapeSpheresKv3(skeletonBones, heroName);
            softbodyChildren.AddRange(shapes);

            // Add chains
            foreach (var chain in detectedChains)
            {
                softbodyChildren.Add(GenerateClothChainKv3(chain));
            }

            var topLevelNodes = new List<string>();

            if (softbodyChildren.Count > 0)
            {
                // Wrap all into a single authentic Softbody node
                var softbodySb = new StringBuilder();
                softbodySb.AppendLine("{\n\t\t\t\t_class = \"Softbody\"\n\t\t\t\tchildren = \n\t\t\t\t[");
                for (int s = 0; s < softbodyChildren.Count; s++)
                {
                    var comma = (s < softbodyChildren.Count - 1) ? "," : "";
                    softbodySb.AppendLine(softbodyChildren[s] + comma);
                }
                softbodySb.AppendLine("\t\t\t\t]\n\t\t\t}");
                topLevelNodes.Add(softbodySb.ToString().Trim());
            }

            if (topLevelNodes.Count == 0)
            {
                return (false, $"No hair, braid, tail, skirt, bola, cuff, or cloth bones found in model skeleton ({Path.GetFileName(targetVmdlPath)}).", 0);
            }

            // Clean target VMDL children
            var cleanTarget = StripExistingClothNodes(targetVmdlContent);

            int rootIdx = cleanTarget.IndexOf("rootNode", StringComparison.OrdinalIgnoreCase);
            if (rootIdx < 0) return (false, "Could not find rootNode in target VMDL.", 0);

            int childrenIdx = cleanTarget.IndexOf("children", rootIdx, StringComparison.OrdinalIgnoreCase);
            if (childrenIdx < 0) return (false, "Could not find children array in target VMDL.", 0);

            int openBracketIdx = cleanTarget.IndexOf('[', childrenIdx);
            if (openBracketIdx < 0) return (false, "Could not find children '[' bracket in target VMDL.", 0);

            var sb = new StringBuilder();
            sb.AppendLine();
            for (int k = 0; k < topLevelNodes.Count; k++)
            {
                var rawBlock = topLevelNodes[k].Trim();
                var indentedBlock = "\t\t\t" + rawBlock.Replace("\n", "\n\t\t\t");
                if (!indentedBlock.EndsWith(","))
                    indentedBlock += ",";
                sb.AppendLine(indentedBlock);
            }

            var insertPos = openBracketIdx + 1;
            var updatedContent = cleanTarget.Insert(insertPos, sb.ToString());

            // Clean loose commas
            updatedContent = Regex.Replace(updatedContent, @"\r?\n\s*,\s*\r?\n", "\r\n");

            File.WriteAllText(targetVmdlPath, updatedContent, Encoding.UTF8);

            var chainNames = detectedChains.Select(c => c.ChainName + " (" + c.Bones.Count + " joints)").ToList();

            var summary = $"Successfully configured Softbody Physics:\n\n" +
                          $"• Softbody Chains: {detectedChains.Count} chain(s)\n" +
                          string.Join("\n", chainNames.Select(n => "    • " + n)) + "\n" +
                          $"• Collision Spheres: {shapes.Count}";

            return (true, summary, topLevelNodes.Count);
        }
        catch (Exception ex)
        {
            return (false, $"Error transferring cloth physics: {ex.Message}", 0);
        }
    }
}
