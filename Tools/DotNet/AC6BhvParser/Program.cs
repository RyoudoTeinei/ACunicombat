using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal static class Program
{
    private const uint SupportedMagic = 0x00030005;
    private const int MinimumHeaderSize = 0x40;
    private const int StateTableOffsetFromGraphStart = 0x20;
    private const int StateRecordSize = 0x80;
    private const int TransitionTableOffsetFromBlockStart = 0x20;
    private const int TransitionRecordSize = 0x20;
    private const int ActionRecordSize = 0x40;

    private static readonly ActionSlotDefinition[] ActionSlotDefinitions =
    [
        new("Primary", 0x10),
        new("Slot_20", 0x20),
        new("Slot_26", 0x26),
        new("Directional_0", 0x32),
        new("Directional_1", 0x34),
        new("Directional_2", 0x36),
        new("Directional_3", 0x38),
        new("Slot_58", 0x58),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            ParseResult result = Parse(options);
            WriteJson(options.OutputJson, result);
            WriteCsv(options.OutputCsv, result);

            Console.WriteLine(
                "AC6BhvParser: states={0}, actions={1}, transitions={2}, aligned={3}",
                result.Header.StateCount,
                result.Header.ActionCount,
                result.Summary.TransitionCount,
                result.Alignment.IsSafeToApplyOfficialNames);
            Console.WriteLine($"JSON: {Path.GetFullPath(options.OutputJson)}");
            Console.WriteLine($"CSV:  {Path.GetFullPath(options.OutputCsv)}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AC6BhvParser failed: {exception.Message}");
            return 1;
        }
    }

    private static ParseResult Parse(Options options)
    {
        byte[] data = File.ReadAllBytes(options.BhvPath);
        if (data.Length < MinimumHeaderSize)
        {
            throw new InvalidDataException(
                $"BHV is smaller than the 0x{MinimumHeaderSize:X} minimum header: {data.Length} bytes.");
        }

        uint magic = ReadUInt32(data, 0x00);
        uint declaredFileSize = ReadUInt32(data, 0x04);
        uint graphStartOffsetRaw = ReadUInt32(data, 0x20);
        uint stateCountRaw = ReadUInt32(data, 0x24);
        uint actionTableOffsetRaw = ReadUInt32(data, 0x28);
        uint actionCountRaw = ReadUInt32(data, 0x2C);
        uint nextTableOffsetRaw = ReadUInt32(data, 0x30);

        if (magic != SupportedMagic)
        {
            throw new InvalidDataException(
                $"Unsupported BHV magic/version 0x{magic:X8}; expected 0x{SupportedMagic:X8}.");
        }

        if (declaredFileSize != data.Length)
        {
            throw new InvalidDataException(
                $"BHV declared size {declaredFileSize} does not match actual size {data.Length}.");
        }

        int stateCount = CheckedInt(stateCountRaw, "state count");
        int graphStartOffset = CheckedInt(graphStartOffsetRaw, "graph start offset");
        int stateTableOffset = checked(graphStartOffset + StateTableOffsetFromGraphStart);
        int actionTableOffset = CheckedInt(actionTableOffsetRaw, "action table offset");
        int actionCount = CheckedInt(actionCountRaw, "action count");
        int nextTableOffset = CheckedInt(nextTableOffsetRaw, "next table offset");
        ValidateRange(data, stateTableOffset, checked(stateCount * StateRecordSize), "state table");
        ValidateRange(data, actionTableOffset, checked(actionCount * ActionRecordSize), "action table");

        int expectedActionTableEnd = checked(actionTableOffset + actionCount * ActionRecordSize);
        if (nextTableOffset != expectedActionTableEnd)
        {
            throw new InvalidDataException(
                $"Action table end 0x{expectedActionTableEnd:X} does not match header next-table offset 0x{nextTableOffset:X}.");
        }

        DebugDocument debugDocument = ReadDebugDocument(options.DebugPath);
        HashSet<string>? physicalActionIds = options.PhysicalHkxRoot is null
            ? null
            : Directory.EnumerateFiles(options.PhysicalHkxRoot, "*.hkx", SearchOption.AllDirectories)
                .Select(TryExtractActionIdFromHkxPath)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<uint, string> physicalActionIdsByTaeAnimationId = options.TaeDumpPath is null
            ? []
            : ReadTaeActionMap(options.TaeDumpPath, options.TaeFilePrefix, physicalActionIds);
        bool stateCountMatches = debugDocument.AllStates.Count == stateCount;
        List<TransitionCountMismatch> transitionMismatches = [];
        List<ConditionCountMismatch> conditionMismatches = [];
        List<TransitionTargetMismatch> transitionTargetMismatches = [];
        Dictionary<string, List<int>> debugStateIndicesByName = BuildDebugStateIndex(debugDocument.AllStates);
        List<ActionRecord> actions = ParseActions(
            data,
            actionTableOffset,
            actionCount,
            physicalActionIdsByTaeAnimationId);
        List<StateRecord> states = [];
        int transitionCountTotal = 0;
        int transitionTargetCheckedCount = 0;
        int transitionTargetMatchCount = 0;

        for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
        {
            int recordOffset = checked(stateTableOffset + stateIndex * StateRecordSize);
            int transitionBlockOffset = CheckedInt(
                ReadUInt32(data, recordOffset + 0x08),
                $"state {stateIndex} transition block offset");
            int transitionOffset = checked(transitionBlockOffset + TransitionTableOffsetFromBlockStart);
            int transitionCount = CheckedInt(ReadUInt32(data, recordOffset + 0x0C), $"state {stateIndex} transition count");
            transitionCountTotal = checked(transitionCountTotal + transitionCount);

            if (transitionCount > 0)
            {
                ValidateRange(
                    data,
                    transitionOffset,
                    checked(transitionCount * TransitionRecordSize),
                    $"state {stateIndex} transition table");
            }

            DebugState? debugState = stateIndex < debugDocument.AllStates.Count
                ? debugDocument.AllStates[stateIndex]
                : null;
            if (debugState is not null && debugState.NextStates.Count != transitionCount)
            {
                transitionMismatches.Add(new TransitionCountMismatch(
                    stateIndex,
                    debugState.StateName,
                    transitionCount,
                    debugState.NextStates.Count));
            }

            List<ActionSlot> actionSlots = [];
            foreach (ActionSlotDefinition definition in ActionSlotDefinitions)
            {
                ushort rawActionIndex = ReadUInt16(data, recordOffset + definition.Offset);
                int? actionIndex = rawActionIndex == ushort.MaxValue ? null : rawActionIndex;
                if (actionIndex is not null && actionIndex.Value >= actionCount)
                {
                    throw new InvalidDataException(
                        $"State {stateIndex} slot {definition.Name} references action {actionIndex}, " +
                        $"but action count is {actionCount}.");
                }

                actionSlots.Add(new ActionSlot(
                    definition.Name,
                    definition.Offset,
                    actionIndex,
                    actionIndex is null ? null : actions[actionIndex.Value].AnimationId,
                    actionIndex is null ? null : actions[actionIndex.Value].PhysicalActionId));
            }

            List<TransitionRecord> transitions = [];
            for (int transitionIndex = 0; transitionIndex < transitionCount; transitionIndex++)
            {
                int itemOffset = checked(transitionOffset + transitionIndex * TransitionRecordSize);
                DebugTransition? debugTransition = debugState is not null && transitionIndex < debugState.NextStates.Count
                    ? debugState.NextStates[transitionIndex]
                    : null;
                ushort targetStateIndex = ReadUInt16(data, itemOffset);
                if (targetStateIndex >= stateCount)
                {
                    throw new InvalidDataException(
                        $"State {stateIndex} transition {transitionIndex} targets state {targetStateIndex}, " +
                        $"but state count is {stateCount}.");
                }

                int conditionCount = CheckedInt(
                    ReadUInt32(data, itemOffset + 0x08),
                    $"state {stateIndex} transition {transitionIndex} condition count");
                if (debugTransition is not null && debugTransition.Conditions.Count != conditionCount)
                {
                    conditionMismatches.Add(new ConditionCountMismatch(
                        stateIndex,
                        transitionIndex,
                        conditionCount,
                        debugTransition.Conditions.Count));
                }

                if (debugTransition is not null)
                {
                    string normalizedTargetName = NormalizeStateName(debugTransition.NextStateName);
                    if (debugStateIndicesByName.TryGetValue(normalizedTargetName, out List<int>? expectedTargets))
                    {
                        transitionTargetCheckedCount++;
                        if (expectedTargets.Contains(targetStateIndex))
                        {
                            transitionTargetMatchCount++;
                        }
                        else
                        {
                            transitionTargetMismatches.Add(new TransitionTargetMismatch(
                                stateIndex,
                                transitionIndex,
                                targetStateIndex,
                                debugTransition.NextStateName,
                                expectedTargets));
                        }
                    }
                }

                transitions.Add(new TransitionRecord(
                    transitionIndex,
                    itemOffset,
                    targetStateIndex,
                    debugDocument.AllStates[targetStateIndex].StateName,
                    conditionCount,
                    debugTransition?.NextStateName,
                    debugTransition?.Conditions ?? [],
                    Convert.ToHexString(data.AsSpan(itemOffset, TransitionRecordSize))));
            }

            ActionSlot primary = actionSlots[0];
            states.Add(new StateRecord(
                stateIndex,
                recordOffset,
                debugState?.StateName,
                transitionOffset,
                transitionCount,
                primary.ActionIndex,
                primary.AnimationId,
                actionSlots,
                transitions,
                Convert.ToHexString(data.AsSpan(recordOffset, StateRecordSize))));
        }

        bool transitionCountsMatch = transitionMismatches.Count == 0;
        bool conditionCountsMatch = conditionMismatches.Count == 0;
        bool transitionTargetsMatch = transitionTargetCheckedCount > 0 && transitionTargetMismatches.Count == 0;
        bool safeToApplyNames =
            stateCountMatches &&
            transitionCountsMatch &&
            conditionCountsMatch &&
            transitionTargetsMatch;
        if (!safeToApplyNames)
        {
            foreach (StateRecord state in states)
            {
                state.OfficialStateName = null;
                foreach (TransitionRecord transition in state.Transitions)
                {
                    transition.OfficialNextStateName = null;
                    transition.OfficialConditions = [];
                }
            }
        }

        FileInfo file = new(options.BhvPath);
        return new ParseResult(
            2,
            new SourceInfo(
                file.Name,
                file.Length,
                Convert.ToHexString(SHA256.HashData(data)),
                Path.GetFileName(options.DebugPath),
                options.TaeDumpPath is null ? null : Path.GetFileName(options.TaeDumpPath),
                options.TaeDumpPath is null ? null : options.TaeFilePrefix),
            new HeaderInfo(
                $"0x{magic:X8}",
                declaredFileSize,
                graphStartOffset,
                stateTableOffset,
                stateCount,
                StateRecordSize,
                actionTableOffset,
                actionCount,
                ActionRecordSize,
                nextTableOffset),
            new AlignmentInfo(
                debugDocument.AllStates.Count,
                stateCount,
                stateCountMatches,
                transitionMismatches.Count,
                transitionCountsMatch,
                transitionMismatches,
                conditionMismatches.Count,
                conditionCountsMatch,
                conditionMismatches,
                transitionTargetCheckedCount,
                transitionTargetMatchCount,
                transitionTargetMismatches.Count,
                transitionTargetsMatch,
                transitionTargetMismatches,
                safeToApplyNames,
                safeToApplyNames
                    ? "State, transition, condition, and resolvable transition-target metadata all match the debug data."
                    : "Official names were withheld because the debug metadata does not align with this BHV."),
            new SummaryInfo(
                transitionCountTotal,
                actions.Count(action => action.AnimationId is not null),
                actions.Count(action => action.AnimationId is null),
                actions.Count(action => action.PhysicalActionId is not null),
                states.Count(state => state.PrimaryAnimationId is not null)),
            actions,
            states);
    }

    private static List<ActionRecord> ParseActions(
        byte[] data,
        int tableOffset,
        int count,
        IReadOnlyDictionary<uint, string> physicalActionIdsByTaeAnimationId)
    {
        List<ActionRecord> actions = [];
        for (int index = 0; index < count; index++)
        {
            int recordOffset = checked(tableOffset + index * ActionRecordSize);
            uint primaryAnimationId = ReadUInt32(data, recordOffset + 0x20);
            uint fallbackAnimationId = ReadUInt32(data, recordOffset + 0x3C);
            uint? animationId = primaryAnimationId != uint.MaxValue
                ? primaryAnimationId
                : fallbackAnimationId != uint.MaxValue
                    ? fallbackAnimationId
                    : null;
            int? animationIdSourceOffset = primaryAnimationId != uint.MaxValue
                ? 0x20
                : fallbackAnimationId != uint.MaxValue
                    ? 0x3C
                    : null;
            actions.Add(new ActionRecord(
                index,
                recordOffset,
                animationId,
                animationIdSourceOffset,
                animationId is null ? null : $"a000_{animationId.Value:D6}",
                animationId is not null && physicalActionIdsByTaeAnimationId.TryGetValue(
                    animationId.Value,
                    out string? physicalActionId)
                        ? physicalActionId
                        : null,
                Convert.ToHexString(data.AsSpan(recordOffset, ActionRecordSize))));
        }

        return actions;
    }

    private static Dictionary<string, List<int>> BuildDebugStateIndex(IReadOnlyList<DebugState> states)
    {
        Dictionary<string, List<int>> result = new(StringComparer.Ordinal);
        for (int index = 0; index < states.Count; index++)
        {
            string normalizedName = NormalizeStateName(states[index].StateName);
            if (!result.TryGetValue(normalizedName, out List<int>? indices))
            {
                indices = [];
                result.Add(normalizedName, indices);
            }

            indices.Add(index);
        }

        return result;
    }

    private static string NormalizeStateName(string name)
    {
        string withoutAnnotations = Regex.Replace(name, "《[^》]*》", string.Empty);
        return withoutAnnotations.Replace("\\", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static Dictionary<uint, string> ReadTaeActionMap(
        string path,
        string filePrefix,
        IReadOnlySet<string>? physicalActionIds)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!document.RootElement.TryGetProperty("taeFiles", out JsonElement taeFiles) ||
            taeFiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("TAE dump does not contain a taeFiles array.");
        }

        Dictionary<uint, List<TaeAnimationReference>> referencesByTaeId = [];
        Dictionary<uint, List<TaeAnimationReference>> referencesByNamedActionId = [];
        foreach (JsonElement taeFile in taeFiles.EnumerateArray())
        {
            string file = taeFile.GetProperty("file").GetString() ?? string.Empty;
            if (!Path.GetFileName(file).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (JsonElement animation in taeFile.GetProperty("animations").EnumerateArray())
            {
                long rawAnimationId = animation.GetProperty("id").GetInt64();
                if (rawAnimationId < 0 || rawAnimationId > uint.MaxValue)
                {
                    throw new InvalidDataException($"TAE animation id is outside UInt32: {rawAnimationId}.");
                }

                uint animationId = (uint)rawAnimationId;
                string physicalActionId = Path.GetFileNameWithoutExtension(
                    animation.GetProperty("name").GetString() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(physicalActionId))
                {
                    continue;
                }

                uint? namedActionId = TryParseActionId(physicalActionId);
                uint? sourceAnimationId = ReadOptionalAnimationId(animation, "importHkxSourceAnimId") ??
                                          ReadOptionalAnimationId(animation, "importFromAnimId");
                TaeAnimationReference reference = new(
                    animationId,
                    physicalActionId,
                    namedActionId,
                    sourceAnimationId,
                    animation.TryGetProperty("importsHkx", out JsonElement importsHkx) &&
                    importsHkx.ValueKind == JsonValueKind.True);
                AddReference(referencesByTaeId, animationId, reference);
                if (namedActionId is not null)
                {
                    AddReference(referencesByNamedActionId, namedActionId.Value, reference);
                }
            }
        }

        Dictionary<uint, string> result = [];
        Dictionary<uint, string> resolvedByTaeId = [];
        foreach (uint taeAnimationId in referencesByTaeId.Keys)
        {
            result[taeAnimationId] = ResolvePhysicalActionId(
                taeAnimationId,
                referencesByTaeId,
                resolvedByTaeId,
                physicalActionIds,
                []);
        }

        // BHV action records normally contain a TAE animation ID. A few AC6 entries instead
        // reference the logical HKX name exposed by another TAE row (for example 122410).
        // Use that logical name as a fallback key, but never replace an exact TAE-ID mapping.
        foreach ((uint namedActionId, List<TaeAnimationReference> references) in referencesByNamedActionId)
        {
            if (result.ContainsKey(namedActionId))
            {
                continue;
            }

            string[] resolved = references
                .Select(reference => ResolveReference(
                    reference,
                    referencesByTaeId,
                    resolvedByTaeId,
                    physicalActionIds,
                    []))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (resolved.Length != 1)
            {
                throw new InvalidDataException(
                    $"Logical TAE action {namedActionId} resolves to multiple physical actions: " +
                    string.Join(", ", resolved));
            }

            result[namedActionId] = resolved[0];
        }

        return result;
    }

    private static void AddReference(
        Dictionary<uint, List<TaeAnimationReference>> target,
        uint key,
        TaeAnimationReference reference)
    {
        if (!target.TryGetValue(key, out List<TaeAnimationReference>? references))
        {
            references = [];
            target.Add(key, references);
        }

        references.Add(reference);
    }

    private static string ResolvePhysicalActionId(
        uint taeAnimationId,
        IReadOnlyDictionary<uint, List<TaeAnimationReference>> referencesByTaeId,
        Dictionary<uint, string> cache,
        IReadOnlySet<string>? physicalActionIds,
        HashSet<uint> activePath)
    {
        if (cache.TryGetValue(taeAnimationId, out string? cached))
        {
            return cached;
        }

        if (!activePath.Add(taeAnimationId))
        {
            throw new InvalidDataException($"TAE miniheader import cycle detected at animation {taeAnimationId}.");
        }

        if (!referencesByTaeId.TryGetValue(taeAnimationId, out List<TaeAnimationReference>? references))
        {
            throw new InvalidDataException($"TAE miniheader imports missing animation {taeAnimationId}.");
        }

        string[] resolved = references
            .Select(reference => ResolveReference(
                reference,
                referencesByTaeId,
                cache,
                physicalActionIds,
                activePath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        activePath.Remove(taeAnimationId);
        if (resolved.Length != 1)
        {
            throw new InvalidDataException(
                $"TAE animation {taeAnimationId} resolves to multiple physical actions: " +
                string.Join(", ", resolved));
        }

        cache[taeAnimationId] = resolved[0];
        return resolved[0];
    }

    private static string ResolveReference(
        TaeAnimationReference reference,
        IReadOnlyDictionary<uint, List<TaeAnimationReference>> referencesByTaeId,
        Dictionary<uint, string> cache,
        IReadOnlySet<string>? physicalActionIds,
        HashSet<uint> activePath)
    {
        // Miniheader source IDs are only a fallback for logical names that do not have
        // their own HKX. Without an inventory we cannot distinguish an alias from a
        // normal animation row, so preserve the named action instead of guessing.
        bool canResolveAliases = physicalActionIds is not null;
        bool namedHkxExists = physicalActionIds?.Contains(reference.NamedActionId) == true;
        if (canResolveAliases &&
            !namedHkxExists &&
            reference.SourceAnimationId is uint sourceAnimationId &&
            sourceAnimationId != reference.TaeAnimationId &&
            referencesByTaeId.ContainsKey(sourceAnimationId))
        {
            string sourcePhysicalActionId = ResolvePhysicalActionId(
                sourceAnimationId,
                referencesByTaeId,
                cache,
                physicalActionIds,
                activePath);

            // AC6 uses one miniheader as the event/template source for a directional
            // family. For example 123011/12/13 all import 123010, while their named
            // actions are 122411/12/13 and their physical HKX files are 022711/12/13.
            // Following the source literally collapses every direction to 022710.
            // Preserve the final directional digit when both the logical source family
            // and the resolved physical sibling family are present in the inventory.
            string? directionalSibling = TryResolveDirectionalSibling(
                reference,
                sourceAnimationId,
                sourcePhysicalActionId,
                referencesByTaeId,
                physicalActionIds);
            return directionalSibling ?? sourcePhysicalActionId;
        }

        return reference.NamedActionId;
    }

    private static string? TryResolveDirectionalSibling(
        TaeAnimationReference reference,
        uint sourceAnimationId,
        string sourcePhysicalActionId,
        IReadOnlyDictionary<uint, List<TaeAnimationReference>> referencesByTaeId,
        IReadOnlySet<string>? physicalActionIds)
    {
        if (physicalActionIds is null || reference.NamedActionNumericId is not uint namedId ||
            !referencesByTaeId.TryGetValue(sourceAnimationId, out List<TaeAnimationReference>? sourceReferences))
        {
            return null;
        }

        uint[] sourceNamedIds = sourceReferences
            .Select(item => item.NamedActionNumericId)
            .OfType<uint>()
            .Distinct()
            .ToArray();
        if (sourceNamedIds.Length != 1)
        {
            return null;
        }

        uint sourceNamedId = sourceNamedIds[0];
        if (namedId / 10 != sourceNamedId / 10)
        {
            return null;
        }

        int directionOffset = checked((int)(namedId % 10) - (int)(sourceNamedId % 10));
        if (directionOffset == 0 || Math.Abs(directionOffset) > 3)
        {
            return null;
        }

        uint? physicalId = TryParseActionId(sourcePhysicalActionId);
        if (physicalId is null)
        {
            return null;
        }

        long candidateId = (long)physicalId.Value + directionOffset;
        if (candidateId < 0 || candidateId > 999999)
        {
            return null;
        }

        string bank = sourcePhysicalActionId[..sourcePhysicalActionId.IndexOf('_')];
        string candidate = $"{bank}_{candidateId:D6}";
        return physicalActionIds.Contains(candidate) ? candidate : null;
    }

    private static uint? ReadOptionalAnimationId(JsonElement animation, string propertyName)
    {
        if (!animation.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        long raw = value.GetInt64();
        return raw >= 0 && raw <= uint.MaxValue ? (uint)raw : null;
    }

    private static uint? TryParseActionId(string actionId)
    {
        Match match = Regex.Match(actionId, "^a\\d{3}_(\\d{6})$", RegexOptions.CultureInvariant);
        return match.Success && uint.TryParse(match.Groups[1].Value, out uint parsed)
            ? parsed
            : null;
    }

    private static string? TryExtractActionIdFromHkxPath(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        Match match = Regex.Match(
            fileName,
            "(?:^|_)(a\\d{3}_\\d{6})$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static DebugDocument ReadDebugDocument(string path)
    {
        DebugDocument? document = JsonSerializer.Deserialize<DebugDocument>(
            File.ReadAllText(path, Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (document?.AllStates is null)
        {
            throw new InvalidDataException("Debug JSON does not contain an AllStates array.");
        }

        return document;
    }

    private static void WriteJson(string path, ParseResult result)
    {
        EnsureParentDirectory(path);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
    }

    private static void WriteCsv(string path, ParseResult result)
    {
        EnsureParentDirectory(path);
        StringBuilder csv = new();
        csv.AppendLine(
            "StateIndex,OfficialStateName,SlotName,SlotOffset,ActionIndex,AnimationId,LogicalActionId,PhysicalActionId,TransitionCount,OfficialNamesApplied");
        foreach (StateRecord state in result.States)
        {
            foreach (ActionSlot slot in state.ActionSlots)
            {
                string? actionId = slot.AnimationId is null ? null : $"a000_{slot.AnimationId.Value:D6}";
                AppendCsvRow(csv,
                    state.Index.ToString(CultureInfo.InvariantCulture),
                    state.OfficialStateName,
                    slot.Name,
                    $"0x{slot.RecordOffset:X2}",
                    slot.ActionIndex?.ToString(CultureInfo.InvariantCulture),
                    slot.AnimationId?.ToString(CultureInfo.InvariantCulture),
                    actionId,
                    slot.PhysicalActionId,
                    state.TransitionCount.ToString(CultureInfo.InvariantCulture),
                    result.Alignment.IsSafeToApplyOfficialNames.ToString(CultureInfo.InvariantCulture));
            }
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
    }

    private static void AppendCsvRow(StringBuilder builder, params string?[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            string value = values[index] ?? string.Empty;
            builder.Append('"');
            builder.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
            builder.Append('"');
        }

        builder.AppendLine();
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        ValidateRange(data, offset, sizeof(ushort), $"UInt16 at 0x{offset:X}");
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        ValidateRange(data, offset, sizeof(uint), $"UInt32 at 0x{offset:X}");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
    }

    private static int CheckedInt(uint value, string label)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"{label} exceeds Int32: {value}.");
        }

        return (int)value;
    }

    private static void ValidateRange(byte[] data, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException(
                $"{label} range [0x{offset:X}, 0x{offset + (long)length:X}) is outside file size 0x{data.Length:X}.");
        }
    }

    private sealed record ActionSlotDefinition(string Name, int Offset);

    private sealed class Options
    {
        public required string BhvPath { get; init; }
        public required string DebugPath { get; init; }
        public required string OutputJson { get; init; }
        public required string OutputCsv { get; init; }
        public string? TaeDumpPath { get; init; }
        public string TaeFilePrefix { get; init; } = "5000000";
        public string? PhysicalHkxRoot { get; init; }

        public static Options Parse(string[] args)
        {
            if (args.Length == 4 && args.All(arg => !arg.StartsWith("--", StringComparison.Ordinal)))
            {
                return Validate(new Options
                {
                    BhvPath = args[0],
                    DebugPath = args[1],
                    OutputJson = args[2],
                    OutputCsv = args[3],
                });
            }

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException(Usage);
                }

                values[args[index][2..]] = args[index + 1];
            }

            if (!values.TryGetValue("bhv", out string? bhv) ||
                !values.TryGetValue("debug", out string? debug) ||
                !values.TryGetValue("json", out string? json) ||
                !values.TryGetValue("csv", out string? csv))
            {
                throw new ArgumentException(Usage);
            }

            return Validate(new Options
            {
                BhvPath = bhv,
                DebugPath = debug,
                OutputJson = json,
                OutputCsv = csv,
                TaeDumpPath = values.TryGetValue("tae", out string? tae) ? tae : null,
                TaeFilePrefix = values.TryGetValue("tae-file-prefix", out string? taeFilePrefix)
                    ? taeFilePrefix
                    : "5000000",
                PhysicalHkxRoot = values.TryGetValue("hkx-root", out string? hkxRoot) ? hkxRoot : null,
            });
        }

        private static Options Validate(Options options)
        {
            if (!File.Exists(options.BhvPath))
            {
                throw new FileNotFoundException("BHV file not found.", options.BhvPath);
            }

            if (!File.Exists(options.DebugPath))
            {
                throw new FileNotFoundException("Debug metadata JSON not found.", options.DebugPath);
            }

            if (options.TaeDumpPath is not null && !File.Exists(options.TaeDumpPath))
            {
                throw new FileNotFoundException("TAE dump JSON not found.", options.TaeDumpPath);
            }

            if (options.PhysicalHkxRoot is not null && !Directory.Exists(options.PhysicalHkxRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Physical HKX root not found: {options.PhysicalHkxRoot}");
            }

            return options;
        }

        private const string Usage =
            "Usage: AC6BhvParser --bhv <basenormal.bhv> --debug <basenormal_dbg.json> " +
            "--json <output.json> --csv <output.csv> [--tae <c0000_tae_dump.json>] " +
            "[--tae-file-prefix <5000000>] [--hkx-root <physical HKX directory>]";
    }

    private sealed class DebugDocument
    {
        public List<DebugState> AllStates { get; init; } = [];
    }

    private sealed class DebugState
    {
        public string StateName { get; init; } = string.Empty;
        public List<DebugTransition> NextStates { get; init; } = [];
    }

    private sealed class DebugTransition
    {
        public string NextStateName { get; init; } = string.Empty;
        public List<string> Conditions { get; init; } = [];
    }

    private sealed record TaeAnimationReference(
        uint TaeAnimationId,
        string NamedActionId,
        uint? NamedActionNumericId,
        uint? SourceAnimationId,
        bool ImportsHkx);

    private sealed record SourceInfo(
        string BhvFile,
        long BhvSizeBytes,
        string BhvSha256,
        string DebugMetadataFile,
        string? TaeDumpFile,
        string? TaeFilePrefix);

    private sealed record HeaderInfo(
        string Magic,
        uint DeclaredFileSize,
        int GraphStartOffset,
        int StateTableOffset,
        int StateCount,
        int StateRecordSize,
        int ActionTableOffset,
        int ActionCount,
        int ActionRecordSize,
        int NextTableOffset);

    private sealed record TransitionCountMismatch(
        int StateIndex,
        string OfficialStateName,
        int BhvTransitionCount,
        int DebugTransitionCount);

    private sealed record ConditionCountMismatch(
        int StateIndex,
        int TransitionIndex,
        int BhvConditionCount,
        int DebugConditionCount);

    private sealed record TransitionTargetMismatch(
        int StateIndex,
        int TransitionIndex,
        int BhvTargetStateIndex,
        string DebugTargetStateName,
        List<int> ExpectedTargetStateIndices);

    private sealed record AlignmentInfo(
        int DebugStateCount,
        int BhvStateCount,
        bool StateCountMatches,
        int TransitionCountMismatchCount,
        bool TransitionCountsMatch,
        List<TransitionCountMismatch> TransitionCountMismatches,
        int ConditionCountMismatchCount,
        bool ConditionCountsMatch,
        List<ConditionCountMismatch> ConditionCountMismatches,
        int TransitionTargetCheckedCount,
        int TransitionTargetMatchCount,
        int TransitionTargetMismatchCount,
        bool TransitionTargetsMatch,
        List<TransitionTargetMismatch> TransitionTargetMismatches,
        bool IsSafeToApplyOfficialNames,
        string Decision);

    private sealed record SummaryInfo(
        int TransitionCount,
        int ActionsWithAnimationId,
        int ActionsWithoutAnimationId,
        int ActionsResolvedToPhysicalHkx,
        int StatesWithPrimaryAnimation);

    private sealed record ActionRecord(
        int Index,
        int RecordOffset,
        uint? AnimationId,
        int? AnimationIdSourceOffset,
        string? ActionId,
        string? PhysicalActionId,
        string RawHex);

    private sealed record ActionSlot(
        string Name,
        int RecordOffset,
        int? ActionIndex,
        uint? AnimationId,
        string? PhysicalActionId);

    private sealed class TransitionRecord
    {
        public TransitionRecord(
            int index,
            int recordOffset,
            ushort rawTargetId,
            string resolvedTargetStateName,
            int conditionCount,
            string? officialNextStateName,
            List<string> officialConditions,
            string rawHex)
        {
            Index = index;
            RecordOffset = recordOffset;
            RawTargetId = rawTargetId;
            ResolvedTargetStateName = resolvedTargetStateName;
            ConditionCount = conditionCount;
            OfficialNextStateName = officialNextStateName;
            OfficialConditions = officialConditions;
            RawHex = rawHex;
        }

        public int Index { get; }
        public int RecordOffset { get; }
        public ushort RawTargetId { get; }
        public string ResolvedTargetStateName { get; }
        public int ConditionCount { get; }
        public string? OfficialNextStateName { get; set; }
        public List<string> OfficialConditions { get; set; }
        public string RawHex { get; }
    }

    private sealed class StateRecord
    {
        public StateRecord(
            int index,
            int recordOffset,
            string? officialStateName,
            int transitionTableOffset,
            int transitionCount,
            int? primaryActionIndex,
            uint? primaryAnimationId,
            List<ActionSlot> actionSlots,
            List<TransitionRecord> transitions,
            string rawHex)
        {
            Index = index;
            RecordOffset = recordOffset;
            OfficialStateName = officialStateName;
            TransitionTableOffset = transitionTableOffset;
            TransitionCount = transitionCount;
            PrimaryActionIndex = primaryActionIndex;
            PrimaryAnimationId = primaryAnimationId;
            PrimaryActionId = primaryAnimationId is null ? null : $"a000_{primaryAnimationId.Value:D6}";
            PrimaryPhysicalActionId = actionSlots[0].PhysicalActionId;
            ActionSlots = actionSlots;
            Transitions = transitions;
            RawHex = rawHex;
        }

        public int Index { get; }
        public int RecordOffset { get; }
        public string? OfficialStateName { get; set; }
        public int TransitionTableOffset { get; }
        public int TransitionCount { get; }
        public int? PrimaryActionIndex { get; }
        public uint? PrimaryAnimationId { get; }
        public string? PrimaryActionId { get; }
        public string? PrimaryPhysicalActionId { get; }
        public List<ActionSlot> ActionSlots { get; }
        public List<TransitionRecord> Transitions { get; }
        public string RawHex { get; }
    }

    private sealed record ParseResult(
        int SchemaVersion,
        SourceInfo Source,
        HeaderInfo Header,
        AlignmentInfo Alignment,
        SummaryInfo Summary,
        List<ActionRecord> Actions,
        List<StateRecord> States);
}
