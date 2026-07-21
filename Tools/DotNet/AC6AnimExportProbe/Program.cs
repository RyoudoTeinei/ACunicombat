using System.Reflection;
using Havoc.IO.Tagfile.Binary.Sections;

try
{
var dsasDir = GetDsasDir();
AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
{
    var name = new AssemblyName(args.Name).Name + ".dll";
    var candidate = Path.Combine(dsasDir, name);
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

if (args.Length > 0 && args[0].Equals("--tagfile-types", StringComparison.OrdinalIgnoreCase))
{
    DumpMatchingTypes("Havoc.dll", "Tagfile|Compendium|TypeReader|TypeSection|BinaryType");
    return 0;
}

if (args.Length > 0 && args[0].Equals("--binder-types", StringComparison.OrdinalIgnoreCase))
{
    DumpMatchingTypes("SoulsFormats.dll", "BND|Binder|DCX|BXF|FileHeader");
    return 0;
}

if (args.Length > 0 && args[0].Equals("--flver-types", StringComparison.OrdinalIgnoreCase))
{
    DumpMatchingTypes("SoulsFormats.dll", "FLVER|Flver|Vertex|Bone|Dummy|GX|Material|Layout");
    return 0;
}

if (args.Length > 0 && args[0].Equals("--tae-types", StringComparison.OrdinalIgnoreCase))
{
    DumpMatchingTypes("SoulsFormats.dll", "TAE|Animation|AnimFile|MiniHeader");
    return 0;
}

if (args.Length > 1 && args[0].Equals("--flver-summary", StringComparison.OrdinalIgnoreCase))
{
    DumpFlverSummary(args[1]);
    return 0;
}

if (args.Length > 2 && args[0].Equals("--compare-flver-pose", StringComparison.OrdinalIgnoreCase))
{
    CompareFlverPose(args[1], args[2], args.Skip(3).Any(arg => arg.Equals("--summary", StringComparison.OrdinalIgnoreCase)));
    return 0;
}

if (args.Length > 1 && args[0].Equals("--list-bnd", StringComparison.OrdinalIgnoreCase))
{
    ListBnd(args[1]);
    return 0;
}

if (args.Length > 2 && args[0].Equals("--extract-bnd", StringComparison.OrdinalIgnoreCase))
{
    ExtractBnd(args[1], args[2], args.Skip(3).ToArray());
    return 0;
}

if (args.Length > 2 && args[0].Equals("--extract-tpf", StringComparison.OrdinalIgnoreCase))
{
    ExtractTpf(args[1], args[2]);
    return 0;
}

if (args.Length > 1 && args[0].Equals("--sections", StringComparison.OrdinalIgnoreCase))
{
    DumpSections(args[1]);
    return 0;
}

if (args.Length > 1 && args[0].Equals("--read-compendium", StringComparison.OrdinalIgnoreCase))
{
    DumpCompendium(args[1]);
    return 0;
}

if (args.Length > 3 && args[0].Equals("--make-compendium", StringComparison.OrdinalIgnoreCase))
{
    MakeCompendium(args[1], args[2], args[3]);
    return 0;
}

if (args.Length > 4 && args[0].Equals("--make-compendium-set", StringComparison.OrdinalIgnoreCase))
{
    MakeCompendiumSet(args.Skip(3).ToArray(), args[1], args[2]);
    return 0;
}

if (args.Length > 3 && args[0].Equals("--dump-tae", StringComparison.OrdinalIgnoreCase))
{
    DumpTae(args[1], args[2], args[3]);
    return 0;
}

if (args.Length > 3 && args[0].Equals("--dump-param", StringComparison.OrdinalIgnoreCase))
{
    DumpParam(args[1], args[2], args[3]);
    return 0;
}

if (args.Length > 2 && args[0].Equals("--dump-param-file", StringComparison.OrdinalIgnoreCase))
{
    DumpParamFile(args[1], args[2]);
    return 0;
}

DumpKnownAnimationTypes();
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("AC6AnimExportProbe failed:");
    Console.Error.WriteLine(ex);
    return 1;
}

static string GetDsasDir()
{
    var configured = Environment.GetEnvironmentVariable("AC6_DSAS_DIR");
    if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        return Path.GetFullPath(configured);

    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        var candidate = Path.Combine(current.FullName, "DSAnimStudio", "DSAS-V5-RC6.1");
        if (Directory.Exists(candidate))
            return Path.GetFullPath(candidate);

        var siblingTools = Path.Combine(current.FullName, "Tools", "DSAnimStudio", "DSAS-V5-RC6.1");
        if (Directory.Exists(siblingTools))
            return Path.GetFullPath(siblingTools);

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("DSAnimStudio DLL folder not found. Set AC6_DSAS_DIR to the DSAS-V5-RC6.1 directory.");
}

void DumpKnownAnimationTypes()
{
    var asm = Assembly.LoadFrom(Path.Combine(GetDsasDir(), "SoulsAssetPipeline.dll"));
    var typeNames = new[]
    {
        "SoulsAssetPipeline.Animation.HKX",
        "SoulsAssetPipeline.Animation.HKX+HKXVariation",
        "SoulsAssetPipeline.Animation.HKX+HKASkeleton",
        "SoulsAssetPipeline.Animation.HKX+HKAAnimationBinding",
        "SoulsAssetPipeline.Animation.HKX+HKASplineCompressedAnimation",
        "SoulsAssetPipeline.Animation.HKX+HKAInterleavedUncompressedAnimation",
        "SoulsAssetPipeline.Animation.HKX+HKADefaultAnimatedReferenceFrame",
        "SoulsAssetPipeline.Animation.HavokAnimationData",
        "SoulsAssetPipeline.Animation.HavokAnimationData_SplineCompressed",
        "SoulsAssetPipeline.Animation.HavokAnimationData_InterleavedUncompressed",
        "SoulsAssetPipeline.Animation.NewBlendableTransform",
        "SoulsAssetPipeline.Animation.RootMotionData",
    };

    foreach (var typeName in typeNames)
    {
        var type = asm.GetType(typeName, throwOnError: false);
        if (type == null)
        {
            Console.WriteLine($"# Missing {typeName}");
            continue;
        }

        DumpType(type);
    }
}

void DumpMatchingTypes(string assemblyName, string regex)
{
    var asm = Assembly.LoadFrom(Path.Combine(GetDsasDir(), assemblyName));
    var types = SafeGetTypes(asm)
        .Where(t => t.FullName != null && System.Text.RegularExpressions.Regex.IsMatch(t.FullName, regex))
        .OrderBy(t => t.FullName, StringComparer.Ordinal);

    foreach (var type in types)
        DumpType(type);
}

static void DumpSections(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    var root = new HkSection(reader);
    DumpSection(root, depth: 0);
}

static void DumpCompendium(string path)
{
    var readerType = typeof(Havoc.IO.Tagfile.Binary.HkBinaryTagfileReader);
    var readMethod = readerType.GetMethod("ReadCompendiums", BindingFlags.Public | BindingFlags.Static, new[] { typeof(byte[]), typeof(bool) })
        ?? throw new MissingMethodException(readerType.FullName, "ReadCompendiums(byte[], bool)");
    var reader = readMethod.Invoke(null, new object[] { File.ReadAllBytes(path), false })
        ?? throw new InvalidOperationException("ReadCompendiums returned null.");

    var ids = GetPrivateField<System.Collections.IEnumerable>(reader, "mCompendiumIDs");
    Console.WriteLine("# IDs");
    foreach (var id in ids)
        Console.WriteLine($"  {id}");

    var types = GetPrivateField<System.Collections.IEnumerable>(reader, "mTypes").Cast<object>().ToList();
    Console.WriteLine($"# Types {types.Count}");
    foreach (var type in types.Take(80))
        Console.WriteLine($"  {DescribeHkType(type)}");
}

static void ListBnd(string path)
{
    using var reader = SoulsFormats.BND4Reader.Read(path);
    Console.WriteLine($"Version={reader.Version} Format={reader.Format} Files={reader.Files.Count}");
    for (var i = 0; i < reader.Files.Count; i++)
    {
        var file = reader.Files[i];
        var bytes = reader.ReadFile(file);
        Console.WriteLine($"{i,4} id={file.ID,10} size={bytes.Length,8} name={file.Name}");
    }
}

static void ExtractBnd(string path, string outputDir, string[] idOrNameFilters)
{
    Directory.CreateDirectory(outputDir);
    using var reader = SoulsFormats.BND4Reader.Read(path);
    var filters = idOrNameFilters.Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
    var extracted = 0;

    foreach (var file in reader.Files)
    {
        if (filters.Length > 0 && !filters.Any(f => MatchesBinderFilter(file, f)))
            continue;

        var bytes = reader.ReadFile(file);
        var name = Path.GetFileName(file.Name);
        if (string.IsNullOrWhiteSpace(name))
            name = $"{file.ID}.bin";

        var outputPath = Path.Combine(outputDir, $"{file.ID}_{name}");
        File.WriteAllBytes(outputPath, bytes);
        extracted++;
        Console.WriteLine($"{file.ID} -> {outputPath} ({bytes.Length} bytes)");
    }

    Console.WriteLine($"Extracted={extracted}");
}

static void ExtractTpf(string path, string outputDir)
{
    Directory.CreateDirectory(outputDir);
    var tpf = SoulsFormats.TPF.Read(path);

    foreach (var texture in tpf.Textures)
    {
        var name = Path.GetFileName(texture.Name);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("TPF contains a texture without a name.");
        if (!name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            name += ".dds";

        var outputPath = Path.Combine(outputDir, name);
        File.WriteAllBytes(outputPath, texture.Bytes);
        Console.WriteLine($"{texture.Name} -> {outputPath} ({texture.Bytes.Length} bytes)");
    }

    Console.WriteLine($"Extracted={tpf.Textures.Count}");
}

static void DumpFlverSummary(string path)
{
    var flver = SoulsFormats.FLVER2.Read(path);

    Console.WriteLine($"Path={path}");
    Console.WriteLine($"Version={flver.Header.Version} Nodes={flver.Nodes.Count} Meshes={flver.Meshes.Count} Materials={flver.Materials.Count} Dummies={flver.Dummies.Count}");
    Console.WriteLine("# Nodes");
    for (var i = 0; i < flver.Nodes.Count; i++)
    {
        var node = flver.Nodes[i];
        Console.WriteLine($"{i,4} parent={node.ParentIndex,4} child={node.FirstChildIndex,4} next={node.NextSiblingIndex,4} flags={node.Flags,-12} name={node.Name} T={FormatVec3(node.Translation)} R={FormatVec3(node.Rotation)} S={FormatVec3(node.Scale)}");
    }

    Console.WriteLine("# Meshes");
    for (var i = 0; i < flver.Meshes.Count; i++)
    {
        var mesh = flver.Meshes[i];
        var materialName = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < flver.Materials.Count
            ? flver.Materials[mesh.MaterialIndex].Name
            : "<invalid>";
        Console.WriteLine($"{i,4} node={mesh.NodeIndex,4} material={mesh.MaterialIndex,4} verts={mesh.Vertices.Count,6} " +
            $"dynamic={mesh.Dynamic} boneIndices={mesh.BoneIndices.Count,4} useWeights={mesh.UseBoneWeights} mat={materialName}");
        if (mesh.BoneIndices.Count > 0)
        {
            var names = mesh.BoneIndices.Take(16)
                .Select(index => index >= 0 && index < flver.Nodes.Count ? $"{index}:{flver.Nodes[index].Name}" : $"{index}:<invalid>");
            Console.WriteLine($"      bones={string.Join(", ", names)}");
        }

        if (mesh.Vertices.Count > 0)
        {
            var vertex = mesh.Vertices[0];
            Console.WriteLine($"      v0 pos={FormatVec3(vertex.Position)} bi={FormatBoneIndices(vertex.BoneIndices)} bw={FormatBoneWeights(vertex.BoneWeights)}");
            var normalWBones = mesh.Vertices
                .GroupBy(candidate => candidate.NormalW)
                .OrderByDescending(group => group.Count())
                .Take(12)
                .Select(group =>
                {
                    var boneIndex = group.Key;
                    var boneName = boneIndex >= 0 && boneIndex < flver.Nodes.Count
                        ? flver.Nodes[boneIndex].Name
                        : "<invalid>";
                    return $"{boneIndex}:{boneName}={group.Count()}";
                });
            Console.WriteLine($"      normalW={string.Join(", ", normalWBones)}");
        }
    }
}

static string FormatVec3(System.Numerics.Vector3 value)
    => $"({value.X:0.######},{value.Y:0.######},{value.Z:0.######})";

static string FormatBoneIndices(SoulsFormats.FLVER.VertexBoneIndices value)
    => $"({value[0]},{value[1]},{value[2]},{value[3]})";

static string FormatBoneWeights(SoulsFormats.FLVER.VertexBoneWeights value)
    => $"({value[0]:0.######},{value[1]:0.######},{value[2]:0.######},{value[3]:0.######})";

static void CompareFlverPose(string flverPath, string poseJsonPath, bool summaryOnly)
{
    var flver = SoulsFormats.FLVER2.Read(flverPath);
    var hkxBones = LoadPoseBones(poseJsonPath);

    var hkxByName = hkxBones
        .GroupBy(b => b.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    var flverNodes = flver.Nodes
        .Select((node, index) => new FlverNodeInfo(
            index,
            node.Name,
            node.ParentIndex,
            node.ParentIndex >= 0 && node.ParentIndex < flver.Nodes.Count ? flver.Nodes[node.ParentIndex].Name : "",
            node.Flags.ToString(),
            node.Translation.X,
            node.Translation.Y,
            node.Translation.Z))
        .ToList();

    var flverByName = flverNodes
        .GroupBy(n => n.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    var usedNodeIndices = new SortedSet<int>();
    foreach (var mesh in flver.Meshes)
    {
        if (mesh.UseBoneWeights)
        {
            foreach (var vertex in mesh.Vertices)
            {
                for (var i = 0; i < 4; i++)
                {
                    if (vertex.BoneWeights[i] <= 0)
                        continue;

                    var boneIndex = vertex.BoneIndices[i];
                    if (boneIndex >= 0 && boneIndex < flver.Nodes.Count)
                        usedNodeIndices.Add(boneIndex);
                }
            }
        }

        if (mesh.BoneIndices.Count > 0)
        {
            foreach (var boneIndex in mesh.BoneIndices)
            {
                if (boneIndex >= 0 && boneIndex < flver.Nodes.Count)
                    usedNodeIndices.Add(boneIndex);
            }
        }
        else if (mesh.NodeIndex >= 0 && mesh.NodeIndex < flver.Nodes.Count)
        {
            usedNodeIndices.Add(mesh.NodeIndex);
        }
    }

    var usedNames = usedNodeIndices.Select(i => flver.Nodes[i].Name).Distinct(StringComparer.Ordinal).ToList();
    var commonNames = flverByName.Keys.Intersect(hkxByName.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    var usedCommonNames = usedNames.Where(hkxByName.ContainsKey).OrderBy(n => n, StringComparer.Ordinal).ToList();
    var flverOnly = flverByName.Keys.Except(hkxByName.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    var hkxOnly = hkxByName.Keys.Except(flverByName.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    var parentMismatches = commonNames
        .Select(name => (Name: name, Flver: flverByName[name], Hkx: hkxByName[name]))
        .Where(item => !string.Equals(item.Flver.ParentName, item.Hkx.ParentName, StringComparison.Ordinal))
        .ToList();

    var translationDiffs = commonNames
        .Select(name =>
        {
            var flverNode = flverByName[name];
            var hkxBone = hkxByName[name];
            var dx = flverNode.X - hkxBone.X;
            var dy = flverNode.Y - hkxBone.Y;
            var dz = flverNode.Z - hkxBone.Z;
            return (Name: name, Distance: Math.Sqrt(dx * dx + dy * dy + dz * dz), Flver: flverNode, Hkx: hkxBone);
        })
        .OrderByDescending(item => item.Distance)
        .ToList();

    Console.WriteLine($"FLVER={flverPath}");
    Console.WriteLine($"POSE={poseJsonPath}");
    Console.WriteLine($"HKX bones={hkxBones.Count} unique={hkxByName.Count}");
    Console.WriteLine($"FLVER nodes={flver.Nodes.Count} unique={flverByName.Count}");
    Console.WriteLine($"FLVER mesh used nodes={usedNodeIndices.Count} used unique names={usedNames.Count}");
    Console.WriteLine($"Common names={commonNames.Count}");
    Console.WriteLine($"Used names present in HKX={usedCommonNames.Count}/{usedNames.Count}");
    Console.WriteLine($"FLVER-only names={flverOnly.Count}");
    Console.WriteLine($"HKX-only names={hkxOnly.Count}");
    Console.WriteLine($"Parent mismatches among common={parentMismatches.Count}/{commonNames.Count}");
    Console.WriteLine();

    if (summaryOnly)
    {
        var usedMissing = usedNames.Except(hkxByName.Keys, StringComparer.Ordinal).ToList();
        var usedMissingAnchorCounts = usedNodeIndices
            .Select(index => flverNodes[index])
            .Where(node => !hkxByName.ContainsKey(node.Name))
            .GroupBy(node => FindNearestHkxAncestor(flverNodes, hkxByName, node.Index))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Used names present in HKX: {string.Join(", ", usedCommonNames.Take(20))}");
        Console.WriteLine($"Used names missing from HKX sample: {string.Join(", ", usedMissing.Take(20))}");
        Console.WriteLine("# Used missing prefix counts");
        foreach (var group in usedMissing.GroupBy(GetBonePrefix).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        Console.WriteLine("# Used missing nearest HKX ancestor counts");
        foreach (var group in usedMissingAnchorCounts)
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        return;
    }

    Console.WriteLine("# Used FLVER mesh bones missing from HKX");
    foreach (var name in usedNames.Except(hkxByName.Keys, StringComparer.Ordinal).Take(50))
        Console.WriteLine($"  {name}");
    Console.WriteLine();

    Console.WriteLine("# First FLVER-only names");
    foreach (var name in flverOnly.Take(50))
        Console.WriteLine($"  {name}");
    Console.WriteLine();

    Console.WriteLine("# First HKX-only names");
    foreach (var name in hkxOnly.Take(50))
        Console.WriteLine($"  {name}");
    Console.WriteLine();

    Console.WriteLine("# Parent mismatches");
    foreach (var item in parentMismatches.Take(40))
        Console.WriteLine($"  {item.Name}: FLVER parent='{item.Flver.ParentName}' flags={item.Flver.Flags} | HKX parent='{item.Hkx.ParentName}'");
    Console.WriteLine();

    Console.WriteLine("# Largest local translation differences");
    foreach (var item in translationDiffs.Take(40))
    {
        Console.WriteLine($"  {item.Name}: d={item.Distance:0.######} FLVER=({item.Flver.X:0.######},{item.Flver.Y:0.######},{item.Flver.Z:0.######}) HKX=({item.Hkx.X:0.######},{item.Hkx.Y:0.######},{item.Hkx.Z:0.######})");
    }
}

static string GetBonePrefix(string name)
{
    var lastUnderscore = name.LastIndexOf('_');
    if (lastUnderscore > 0 && lastUnderscore + 1 < name.Length && name[(lastUnderscore + 1)..].All(char.IsDigit))
        return name[..lastUnderscore];

    return name;
}

static string FindNearestHkxAncestor(IReadOnlyList<FlverNodeInfo> flverNodes, IReadOnlyDictionary<string, HkxBoneInfo> hkxByName, int nodeIndex)
{
    var visited = new HashSet<int>();
    var current = nodeIndex;
    while (current >= 0 && current < flverNodes.Count && visited.Add(current))
    {
        var node = flverNodes[current];
        if (hkxByName.ContainsKey(node.Name))
            return node.Name;

        current = node.ParentIndex;
    }

    return "<none>";
}

static List<HkxBoneInfo> LoadPoseBones(string poseJsonPath)
{
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(poseJsonPath));
    var boneElements = document.RootElement.GetProperty("Bones").EnumerateArray().ToList();
    var result = new List<HkxBoneInfo>(boneElements.Count);
    var parentIndices = new List<int>(boneElements.Count);

    foreach (var bone in boneElements)
    {
        var transform = bone.GetProperty("ReferenceLocalTransform");
        var translation = transform.GetProperty("Translation");
        result.Add(new HkxBoneInfo(
            bone.GetProperty("Index").GetInt32(),
            bone.GetProperty("Name").GetString() ?? "",
            bone.GetProperty("ParentIndex").GetInt32(),
            "",
            translation.GetProperty("X").GetDouble(),
            translation.GetProperty("Y").GetDouble(),
            translation.GetProperty("Z").GetDouble()));
        parentIndices.Add(bone.GetProperty("ParentIndex").GetInt32());
    }

    for (var i = 0; i < result.Count; i++)
    {
        var parentIndex = parentIndices[i];
        var parentName = parentIndex >= 0 && parentIndex < result.Count ? result[parentIndex].Name : "";
        result[i] = result[i] with { ParentName = parentName };
    }

    return result;
}

static bool MatchesBinderFilter(SoulsFormats.BinderFileHeader file, string filter)
{
    if (int.TryParse(filter, out var id) && file.ID == id)
        return true;

    return file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(file.Name).Equals(filter, StringComparison.OrdinalIgnoreCase);
}

static void MakeCompendium(string animationPath, string typeSourcePath, string outputPath)
{
    MakeCompendiumSet(new[] { animationPath }, typeSourcePath, outputPath);
}

static void MakeCompendiumSet(IReadOnlyList<string> tagfilePaths, string typeSourcePath, string outputPath)
{
    if (tagfilePaths == null || tagfilePaths.Count == 0)
        throw new ArgumentException("At least one tagfile path is required.", nameof(tagfilePaths));

    var typeSource = File.ReadAllBytes(typeSourcePath);
    var compendiumIds = new List<byte[]>();
    var knownIds = new HashSet<string>(StringComparer.Ordinal);
    for (var i = 0; i < tagfilePaths.Count; i++)
    {
        var path = tagfilePaths[i];
        var tagfile = File.ReadAllBytes(path);
        var tcrf = FindAscii(tagfile, "TCRF");
        if (tcrf < 0)
            throw new InvalidDataException($"No TCRF section found in {path}.");

        var compendiumId = new byte[8];
        Array.Copy(tagfile, tcrf + 4, compendiumId, 0, compendiumId.Length);
        var key = Convert.ToHexString(compendiumId);
        if (knownIds.Add(key))
            compendiumIds.Add(compendiumId);
    }

    var typeMarker = FindAscii(typeSource, "TYPE");
    if (typeMarker < 4)
        throw new InvalidDataException($"No TYPE section found in {typeSourcePath}.");

    var typeSectionStart = typeMarker - 4;
    var typeSectionLength = ReadBigEndianInt32(typeSource, typeSectionStart);
    var typeSection = new byte[typeSectionLength];
    Array.Copy(typeSource, typeSectionStart, typeSection, 0, typeSection.Length);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    using var stream = File.Create(outputPath);
    using var writer = new BinaryWriter(stream);
    using (new HkSectionWriter(writer, "TCM0", hasSubSections: true))
    {
        using (new HkSectionWriter(writer, "TCID", hasSubSections: false))
        {
            for (var i = 0; i < compendiumIds.Count; i++)
                writer.Write(compendiumIds[i]);
        }
        writer.Write(typeSection);
    }

    Console.WriteLine($"Wrote {outputPath}");
    Console.WriteLine($"  ids={string.Join(",", compendiumIds.Select(Convert.ToHexString))}");
    Console.WriteLine($"  typeSection=0x{typeSectionStart:X}+0x{typeSectionLength:X}");
}

static int FindAscii(byte[] bytes, string value)
{
    var pattern = System.Text.Encoding.ASCII.GetBytes(value);
    for (var i = 0; i <= bytes.Length - pattern.Length; i++)
    {
        var ok = true;
        for (var j = 0; j < pattern.Length; j++)
        {
            if (bytes[i + j] != pattern[j])
            {
                ok = false;
                break;
            }
        }

        if (ok)
            return i;
    }

    return -1;
}

static int ReadBigEndianInt32(byte[] bytes, int offset)
    => (bytes[offset] << 24)
       | (bytes[offset + 1] << 16)
       | (bytes[offset + 2] << 8)
       | bytes[offset + 3];

static T GetPrivateField<T>(object instance, string name)
{
    var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(instance.GetType().FullName, name);
    return (T)field.GetValue(instance)!;
}

static string DescribeHkType(object type)
{
    static object? ReadMember(object obj, string name)
    {
        var t = obj.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null)
            return prop.GetValue(obj);
        var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(obj);
    }

    var name = ReadMember(type, "Name");
    var hash = ReadMember(type, "Hash");
    var parent = ReadMember(type, "Parent");
    return $"{name ?? type} hash={hash ?? "-"} parent={parent ?? "-"}";
}

static void DumpSection(HkSection section, int depth)
{
    if (section == null)
    {
        Console.WriteLine($"{new string(' ', depth * 2)}<null>");
        return;
    }

    Console.WriteLine($"{new string(' ', depth * 2)}{section.Signature ?? "<null>"} pos=0x{section.Position:X} len=0x{section.Length:X} sub={section.SubSections?.Count ?? 0}");
    if (section.SubSections == null)
        return;

    foreach (var sub in section.SubSections)
        DumpSection(sub, depth + 1);
}

static Type[] SafeGetTypes(Assembly assembly)
{
    try
    {
        return assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        return ex.Types.Where(t => t != null).Cast<Type>().ToArray();
    }
}

static void DumpType(Type type)
{
    Console.WriteLine($"# {type.FullName}");
    Console.WriteLine("Constructors:");
    foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        Console.WriteLine($"  {AccessMethod(ctor)} {type.Name}({Params(ctor)})");

    Console.WriteLine("Fields:");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                 .OrderBy(f => f.Name))
        Console.WriteLine($"  {AccessField(field)} {field.FieldType.FullName} {field.Name}");

    Console.WriteLine("Properties:");
    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                 .OrderBy(p => p.Name))
        Console.WriteLine($"  {AccessProperty(prop)} {prop.PropertyType.FullName} {prop.Name}");

    Console.WriteLine("Methods:");
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                 .Where(m => !m.IsSpecialName)
                 .OrderBy(m => m.Name))
        Console.WriteLine($"  {AccessMethod(method)} {method.ReturnType.FullName} {method.Name}({Params(method)})");

    Console.WriteLine();
}

static string AccessMethod(MethodBase method)
{
    var access = method.IsPublic ? "public"
        : method.IsPrivate ? "private"
        : method.IsFamily ? "protected"
        : method.IsAssembly ? "internal"
        : "nonpublic";
    return method.IsStatic ? access + " static" : access;
}

// Dumps every TAE file (a single .tae or a directory of them) to one JSON manifest. Event
// names and parameter layouts come from a DSAS V5 action template (TAE.Template.AC6.xml,
// root element action_template) which we parse ourselves — SoulsFormats.TAE.Template expects
// the older schema and cannot read these files. Parameters are decoded little-endian from the
// event's raw bytes following the field order declared in the template.
static void DumpTae(string taePathOrDir, string templatePath, string outputPath)
{
    var actionTemplates = LoadActionTemplates(templatePath);
    var files = Directory.Exists(taePathOrDir)
        ? Directory.GetFiles(taePathOrDir, "*.tae").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray()
        : new[] { taePathOrDir };

    var taeList = new List<object>();
    foreach (var file in files)
    {
        var tae = SoulsFormats.TAE.Read(file);
        var animations = new List<object>();
        foreach (var anim in tae.Animations)
        {
            var headerType = anim.MiniHeader.Type.ToString();
            bool? importsHkx = null;
            int? importHkxSourceAnimId = null;
            int? importFromAnimId = null;
            int? importUnknown = null;
            if (anim.MiniHeader is SoulsFormats.TAE.Animation.AnimMiniHeader.Standard standardHeader)
            {
                importsHkx = standardHeader.ImportsHKX;
                importHkxSourceAnimId = standardHeader.ImportHKXSourceAnimID;
            }
            else if (anim.MiniHeader is SoulsFormats.TAE.Animation.AnimMiniHeader.ImportOtherAnim importHeader)
            {
                importFromAnimId = importHeader.ImportFromAnimID;
                importUnknown = importHeader.Unknown;
            }

            var events = new List<object>();
            foreach (var ev in anim.Events)
            {
                string? typeName = null;
                Dictionary<string, object?>? parameters = null;
                if (actionTemplates.TryGetValue(ev.Type, out var actionTemplate))
                {
                    typeName = actionTemplate.Name;
                    parameters = DecodeEventParameters(ev.GetParameterBytes(tae.BigEndian), actionTemplate);
                }

                events.Add(new
                {
                    type = ev.Type,
                    typeName,
                    startTime = ev.StartTime,
                    endTime = ev.EndTime,
                    parameters,
                });
            }

            animations.Add(new
            {
                id = anim.ID,
                name = anim.AnimFileName,
                headerType,
                importsHkx,
                importHkxSourceAnimId,
                importFromAnimId,
                importUnknown,
                eventCount = anim.Events.Count,
                events,
            });
        }

        taeList.Add(new
        {
            file = Path.GetFileName(file),
            eventBank = tae.EventBank,
            animationCount = tae.Animations.Count,
            animations,
        });
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    File.WriteAllText(outputPath, System.Text.Json.JsonSerializer.Serialize(new { taeFiles = taeList },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Dumped {taeList.Count} TAE files to {outputPath}");
}

// Decrypts an AC6 regulation.bin, extracts one param table and prints every row with named
// fields from a Paramdex paramdef XML.
static void DumpParam(string regulationPath, string paramName, string paramdefPath)
{
    // The decrypt helper's declaring type differs between SoulsFormats builds; resolve by name.
    var decrypt = typeof(SoulsFormats.SFUtil).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        .First(mth => mth.Name == "DecryptAC6Regulation" && mth.GetParameters().Length == 1);
    var bnd = (SoulsFormats.BND4)decrypt.Invoke(null, new object[] { regulationPath })!;
    if (paramName == "list")
    {
        foreach (var f in bnd.Files)
            Console.WriteLine(f.Name);
        return;
    }

    var file = bnd.Files.FirstOrDefault(f =>
        f.Name != null && f.Name.EndsWith(paramName + ".param", StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException($"{paramName}.param not found in {regulationPath}");

    var param = SoulsFormats.PARAM.Read(file.Bytes);
    var def = SoulsFormats.PARAMDEF.XmlDeserialize(paramdefPath);
    param.ApplyParamdef(def);

    Console.WriteLine($"# {paramName}: {param.Rows.Count} rows, {def.Fields.Count} fields");
    foreach (var row in param.Rows)
    {
        var cells = row.Cells
            .Where(c => c.Def.InternalType != "dummy8")
            .Select(c => $"{c.Def.InternalName}={c.Value}");
        Console.WriteLine($"row {row.ID}: {string.Join(" ", cells)}");
    }
}

// Reads a PARAM that has already been extracted from gameparam.parambnd.dcx.
// This avoids relying on a specific regulation.bin build when only raw game
// parameters are available.
static void DumpParamFile(string paramPath, string paramdefPath)
{
    var param = SoulsFormats.PARAM.Read(paramPath);
    var def = SoulsFormats.PARAMDEF.XmlDeserialize(paramdefPath);
    param.ApplyParamdef(def);

    Console.WriteLine($"# {Path.GetFileNameWithoutExtension(paramPath)}: {param.Rows.Count} rows, {def.Fields.Count} fields");
    foreach (var row in param.Rows)
    {
        var cells = row.Cells
            .Where(c => c.Def.InternalType != "dummy8")
            .Select(c => $"{c.Def.InternalName}={c.Value}");
        Console.WriteLine($"row {row.ID}: {string.Join(" ", cells)}");
    }
}

static Dictionary<int, ActionTemplate> LoadActionTemplates(string templatePath)
{
    var xml = new System.Xml.XmlDocument();
    xml.Load(templatePath);
    var result = new Dictionary<int, ActionTemplate>();
    var actions = xml.SelectNodes("//action");
    if (actions == null)
        return result;

    foreach (System.Xml.XmlNode action in actions)
    {
        if (!int.TryParse(action.Attributes?["id"]?.Value, out var id))
            continue;

        var fields = new List<ActionField>();
        foreach (System.Xml.XmlNode field in action.ChildNodes)
        {
            if (field.NodeType != System.Xml.XmlNodeType.Element)
                continue;

            Dictionary<long, string>? enums = null;
            foreach (System.Xml.XmlNode entry in field.ChildNodes)
            {
                if (entry.Name != "entry")
                    continue;
                if (long.TryParse(entry.Attributes?["value"]?.Value, out var value))
                {
                    enums ??= new Dictionary<long, string>();
                    enums[value] = entry.Attributes?["name"]?.Value ?? value.ToString();
                }
            }

            fields.Add(new ActionField(field.Name, field.Attributes?["name"]?.Value ?? field.Name, enums));
        }

        result[id] = new ActionTemplate(action.Attributes?["name"]?.Value ?? $"Action{id}", fields);
    }

    return result;
}

static Dictionary<string, object?> DecodeEventParameters(byte[] bytes, ActionTemplate template)
{
    var result = new Dictionary<string, object?>();
    var offset = 0;
    foreach (var field in template.Fields)
    {
        long? intValue = null;
        object? value = null;
        try
        {
            switch (field.Type)
            {
                case "s8": if (offset + 1 > bytes.Length) return result; intValue = (sbyte)bytes[offset]; offset += 1; break;
                case "u8": if (offset + 1 > bytes.Length) return result; intValue = bytes[offset]; offset += 1; break;
                case "s16": if (offset + 2 > bytes.Length) return result; intValue = BitConverter.ToInt16(bytes, offset); offset += 2; break;
                case "u16": if (offset + 2 > bytes.Length) return result; intValue = BitConverter.ToUInt16(bytes, offset); offset += 2; break;
                case "s32": if (offset + 4 > bytes.Length) return result; intValue = BitConverter.ToInt32(bytes, offset); offset += 4; break;
                case "u32": if (offset + 4 > bytes.Length) return result; intValue = BitConverter.ToUInt32(bytes, offset); offset += 4; break;
                case "s64": if (offset + 8 > bytes.Length) return result; intValue = BitConverter.ToInt64(bytes, offset); offset += 8; break;
                case "u64": if (offset + 8 > bytes.Length) return result; intValue = (long)BitConverter.ToUInt64(bytes, offset); offset += 8; break;
                case "f32": if (offset + 4 > bytes.Length) return result; value = BitConverter.ToSingle(bytes, offset); offset += 4; break;
                case "f64": if (offset + 8 > bytes.Length) return result; value = BitConverter.ToDouble(bytes, offset); offset += 8; break;
                default: return result; // aob or unknown layout: stop decoding to avoid misalignment
            }
        }
        catch
        {
            return result;
        }

        if (intValue.HasValue)
        {
            value = field.Enums != null && field.Enums.TryGetValue(intValue.Value, out var label)
                ? $"{intValue.Value} ({label})"
                : intValue.Value;
        }

        result[field.Name] = value;
    }

    return result;
}

static string AccessField(FieldInfo field)
{
    if (field.IsPublic) return "public";
    if (field.IsPrivate) return "private";
    if (field.IsFamily) return "protected";
    if (field.IsAssembly) return "internal";
    return "nonpublic";
}

static string AccessProperty(PropertyInfo prop)
{
    var method = prop.GetMethod ?? prop.SetMethod;
    return method == null ? "unknown" : AccessMethod(method);
}

static string Params(MethodBase method)
    => string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));

readonly record struct FlverNodeInfo(int Index, string Name, int ParentIndex, string ParentName, string Flags, double X, double Y, double Z);
readonly record struct HkxBoneInfo(int Index, string Name, int ParentIndex, string ParentName, double X, double Y, double Z);
sealed record ActionField(string Type, string Name, Dictionary<long, string>? Enums);
sealed record ActionTemplate(string Name, List<ActionField> Fields);
