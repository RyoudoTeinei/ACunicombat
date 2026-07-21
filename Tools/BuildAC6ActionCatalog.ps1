param(
    [string]$RawRoot = 'E:\UnityDev\Resources\AC6_ExtractedAssets\chr\AC - c0000',
    [string]$TaeDump = 'Tools/LocalOut/c0000_tae_dump.json',
    [string]$Labels = 'Tools/AssetManifests/c0000_action_labels.json',
    [string]$BhvStateMap = 'Tools/AssetManifests/c0000_bhv_state_map.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepoPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $repoRoot $path
}

function Has-Property($object, [string]$name) {
    return $null -ne $object -and $object.PSObject.Properties.Name -contains $name
}

function Escape-Markdown([string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return '' }
    return ($value -replace '\|', '\|') -replace "`r?`n", '<br>'
}

function Get-Classification([string]$actionId, [string[]]$eventTypes) {
    $bank = if ($actionId -match '^(a\d{3})_') { $Matches[1] } else { 'unknown' }
    $suffix = if ($actionId -match '^a\d{3}_(\d{6})$') { $Matches[1] } else { '' }
    $eventSet = @{}
    foreach ($eventType in $eventTypes) { $eventSet[$eventType] = $true }

    $categoryKey = 'Unknown'
    $categoryZh = '未分类动作'
    $layer = 'FullBody'
    $namePrefix = '待人工确认：未解析动作'
    $evidence = New-Object System.Collections.Generic.List[string]

    if ($bank -match '^a(2\d\d|3[0-3]\d)$') {
        $categoryKey = 'WeaponAction'
        $categoryZh = '武器姿态与攻击动作'
        $layer = 'UpperBody'
        $namePrefix = '待人工确认：武器动作'
        $evidence.Add("动作 bank 为 $bank，属于武器动作组；具体武器种类和阶段仍需目检。")
    }
    elseif ($bank -eq 'a000') {
        $categoryKey = 'BodyAction'
        $categoryZh = '机体基础与移动动作'
        $namePrefix = '待人工确认：机体基础动作'
        $evidence.Add('动作位于 a000 机体主动作 bank。')

        if ($suffix -match '^010') {
            $categoryKey = 'BodyMovementCandidate'
            $categoryZh = '机体移动或姿态候选'
            $namePrefix = '待人工确认：机体移动/姿态候选'
            $evidence.Add('编号位于 010xxx 族；只能判定为移动/姿态候选，不能据此确认 QB、AB 或飞行。')
        }
        elseif ($suffix -match '^(105|115)') {
            $categoryKey = 'KnockdownRecoveryCandidate'
            $categoryZh = '倒地或恢复候选'
            $namePrefix = '待人工确认：倒地/恢复候选'
            $evidence.Add('编号位于 105xxx/115xxx 族；具体倒地、起身或恢复阶段需目检。')
        }
        elseif ($suffix -match '^022' -or $eventSet.ContainsKey('PadRumble') -or
                $eventSet.ContainsKey('RumbleCam_Global')) {
            $categoryKey = 'ImpactReactionCandidate'
            $categoryZh = '冲击或受击反应候选'
            $namePrefix = '待人工确认：冲击/受击反应候选'
            $evidence.Add('编号或震动/镜头事件提示冲击反应；不能仅凭事件确认具体受击方向。')
        }
        elseif ($eventSet.ContainsKey('FootSfxParam_Entity') -or
                $eventSet.ContainsKey('[DS3]DecalOnLanding')) {
            $categoryKey = 'GroundContactCandidate'
            $categoryZh = '地面移动或接触候选'
            $namePrefix = '待人工确认：地面移动/接触候选'
            $evidence.Add('包含足部或落地事件。')
        }
    }
    elseif ($bank -eq 'a100') {
        $categoryKey = 'BodySpecialAction'
        $categoryZh = '机体特殊动作'
        $namePrefix = '待人工确认：机体特殊动作'
        $evidence.Add('动作位于 a100 特殊动作 bank。')
    }
    elseif ($bank -eq 'a500') {
        $categoryKey = 'SpecialAction'
        $categoryZh = '特殊演出动作'
        $namePrefix = '待人工确认：特殊演出动作'
        $evidence.Add('动作位于 a500 特殊演出 bank。')
    }
    elseif ($bank -eq 'a900') {
        $categoryKey = 'SystemAction'
        $categoryZh = '系统或调试动作'
        $namePrefix = '待人工确认：系统/调试动作'
        $evidence.Add('动作位于 a900 系统动作 bank。')
    }

    if ($eventSet.ContainsKey('AttackBehavior') -or $eventSet.ContainsKey('PCBehavior')) {
        $evidence.Add('TAE 含攻击/行为事件，说明动作可能参与攻击执行，但不能确定具体武器。')
    }

    return [pscustomobject][ordered]@{
        bank = $bank
        categoryKey = $categoryKey
        categoryZh = $categoryZh
        layer = $layer
        displayNameZh = "$namePrefix $actionId"
        evidence = $evidence.ToArray()
    }
}

$taeDumpPath = Resolve-RepoPath $TaeDump
$labelsPath = Resolve-RepoPath $Labels
$bhvStateMapPath = Resolve-RepoPath $BhvStateMap
$catalogPath = Join-Path $repoRoot 'Tools/AssetManifests/c0000_action_catalog.json'
$csvPath = Join-Path $repoRoot 'Tools/AssetManifests/c0000_action_catalog.csv'
$summaryPath = Join-Path $repoRoot 'Docs/AC6动作资产表.md'
$reviewPath = Join-Path $repoRoot 'Docs/AC6动作待人工确认.md'
$poseRoot = Join-Path $repoRoot 'Tools/LocalOut/actions'
$clipRoot = Join-Path $repoRoot 'Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions'

if (-not (Test-Path -LiteralPath $RawRoot)) { throw "Raw HKX root not found: $RawRoot" }
if (-not (Test-Path -LiteralPath $taeDumpPath)) { throw "TAE dump not found: $taeDumpPath" }

Write-Host 'Reading TAE dump...'
$tae = Get-Content -LiteralPath $taeDumpPath -Raw -Encoding UTF8 | ConvertFrom-Json
$referencesByAction = @{}
$taeRowCount = 0
foreach ($taeFile in $tae.taeFiles) {
    foreach ($animation in $taeFile.animations) {
        $taeRowCount++
        $actionId = [System.IO.Path]::GetFileNameWithoutExtension([string]$animation.name)
        if ([string]::IsNullOrWhiteSpace($actionId)) { continue }

        $eventTypes = @($animation.events | ForEach-Object { [string]$_.typeName } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
        $reference = [pscustomobject][ordered]@{
            taeFile = [string]$taeFile.file
            taeActionId = [int64]$animation.id
            eventBank = [string]$taeFile.eventBank
            eventCount = [int]$animation.eventCount
            eventTypes = $eventTypes
        }
        if (-not $referencesByAction.ContainsKey($actionId)) {
            $referencesByAction[$actionId] = New-Object System.Collections.Generic.List[object]
        }
        $referencesByAction[$actionId].Add($reference)
    }
}

$rawByAction = @{}
$supportFiles = New-Object System.Collections.Generic.List[object]
foreach ($file in Get-ChildItem -LiteralPath $RawRoot -File) {
    if ($file.Name -notmatch '\.hkx$') { continue }
    if ($file.BaseName -match '^a\d{3}_\d{6}$') {
        $rawByAction[$file.BaseName] = $file
    }
    else {
        $supportFiles.Add([pscustomobject][ordered]@{
            name = $file.Name
            sizeBytes = [int64]$file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        })
    }
}

$overridesByAction = @{}
if (Test-Path -LiteralPath $labelsPath) {
    $labelsDocument = Get-Content -LiteralPath $labelsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($label in $labelsDocument.entries) {
        if (-not [string]::IsNullOrWhiteSpace([string]$label.actionId)) {
            $overridesByAction[[string]$label.actionId] = $label
        }
    }
}

$bhvRefsByAction = @{}
$bhvPhysicalAliasesByAction = @{}
$bhvAlignmentSafe = $false
if (Test-Path -LiteralPath $bhvStateMapPath) {
    $bhvDocument = Get-Content -LiteralPath $bhvStateMapPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $bhvAlignmentSafe = [bool]$bhvDocument.alignment.isSafeToApplyOfficialNames
    if (-not $bhvAlignmentSafe) {
        throw 'BHV state map exists, but its debug metadata alignment is not safe. Rebuild it before generating the action catalog.'
    }

    foreach ($action in $bhvDocument.actions) {
        if (-not [string]::IsNullOrWhiteSpace([string]$action.actionId) -and
            -not [string]::IsNullOrWhiteSpace([string]$action.physicalActionId)) {
            $bhvPhysicalAliasesByAction[[string]$action.actionId] = [string]$action.physicalActionId
        }
    }

    foreach ($state in $bhvDocument.states) {
        if ([string]::IsNullOrWhiteSpace([string]$state.officialStateName)) { continue }
        foreach ($slot in $state.actionSlots) {
            if ($null -eq $slot.animationId) { continue }
            $logicalActionId = 'a000_{0:D6}' -f [uint32]$slot.animationId
            $mappedActionId = if (-not [string]::IsNullOrWhiteSpace([string]$slot.physicalActionId)) {
                [string]$slot.physicalActionId
            } else {
                $logicalActionId
            }
            if (-not $bhvRefsByAction.ContainsKey($logicalActionId)) {
                $bhvRefsByAction[$logicalActionId] = New-Object System.Collections.Generic.List[object]
            }
            $bhvRefsByAction[$logicalActionId].Add([pscustomobject][ordered]@{
                stateIndex = [int]$state.index
                officialStateName = [string]$state.officialStateName
                slotName = [string]$slot.name
                actionIndex = [int]$slot.actionIndex
                taeAnimationId = [uint32]$slot.animationId
                logicalActionId = $logicalActionId
                physicalActionId = $mappedActionId
                physicalAliasApplied = ($logicalActionId -ne $mappedActionId)
                primary = ([string]$slot.name -eq 'Primary')
            })
        }
    }
}

$allActionIds = @($rawByAction.Keys + $referencesByAction.Keys + $bhvRefsByAction.Keys | Sort-Object -Unique)
$entries = New-Object System.Collections.Generic.List[object]
foreach ($actionId in $allActionIds) {
    $bhvRefs = if ($bhvRefsByAction.ContainsKey($actionId)) {
        @($bhvRefsByAction[$actionId] | Sort-Object stateIndex, slotName -Unique)
    } else { @() }
    $physicalAliases = @($bhvRefs | ForEach-Object physicalActionId |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($bhvPhysicalAliasesByAction.ContainsKey($actionId)) {
        $physicalAliases = @($physicalAliases + [string]$bhvPhysicalAliasesByAction[$actionId] |
            Sort-Object -Unique)
    }
    if ($physicalAliases.Count -gt 1) {
        throw "Logical action $actionId resolves to multiple physical HKX files: $($physicalAliases -join ', ')"
    }
    $physicalActionId = if ($rawByAction.ContainsKey($actionId)) {
        $actionId
    } elseif ($physicalAliases.Count -eq 1) {
        [string]$physicalAliases[0]
    } else {
        $actionId
    }
    $rawFile = if ($rawByAction.ContainsKey($physicalActionId)) { $rawByAction[$physicalActionId] } else { $null }
    $references = if ($referencesByAction.ContainsKey($actionId)) {
        @($referencesByAction[$actionId] | Sort-Object taeFile, taeActionId)
    } else { @() }
    $eventTypes = @($references | ForEach-Object { $_.eventTypes } | Sort-Object -Unique)
    $classification = Get-Classification $actionId $eventTypes
    $override = if ($overridesByAction.ContainsKey($actionId)) { $overridesByAction[$actionId] } else { $null }
    $bhvPrimaryRefs = @($bhvRefs | Where-Object primary)
    $bhvPrimaryNames = @($bhvPrimaryRefs | ForEach-Object officialStateName | Sort-Object -Unique)

    $posePath = Join-Path $poseRoot ($actionId + '_pose.json')
    $poseAvailable = Test-Path -LiteralPath $posePath
    $clipFolder = Join-Path $clipRoot $actionId
    $clipCount = if (Test-Path -LiteralPath $clipFolder) {
        @(Get-ChildItem -LiteralPath $clipFolder -Filter '*.anim' -File).Count
    } else { 0 }

    $displayNameZh = $classification.displayNameZh
    $categoryKey = $classification.categoryKey
    $categoryZh = $classification.categoryZh
    $layer = $classification.layer
    $reviewStatus = 'NeedsReview'
    $confidence = 'Low'
    $descriptionZh = '程序只能根据动作 bank 与 TAE 事件做宽泛分类；具体动作必须在 Unity 中预览后人工确认。'
    $manualNoteZh = ''
    $loop = $false
    $semanticSource = 'Heuristic'
    if ($bhvPrimaryNames.Count -gt 0) {
        $displayNameZh = '官方状态：' + ($bhvPrimaryNames -join ' / ')
        $categoryKey = 'OfficialBodyState'
        $categoryZh = '机体官方行为状态'
        $reviewStatus = 'OfficialMapped'
        $confidence = 'High'
        $descriptionZh = '状态索引及逐状态转换数量已与本机 basenormal.bhv 严格对齐。官方状态名：' + ($bhvPrimaryNames -join ' / ') + '。'
        $semanticSource = 'OfficialBhvPrimary'
    }
    elseif ($bhvRefs.Count -gt 0) {
        $semanticSource = 'OfficialBhvAuxiliary'
    }
    if ($null -ne $override) {
        $overrideReviewStatus = if (Has-Property $override 'reviewStatus') { [string]$override.reviewStatus } else { '' }
        $applySemanticOverride = $overrideReviewStatus -in @('Confirmed', 'Rejected')
        if ($applySemanticOverride) {
            if ((Has-Property $override 'displayNameZh') -and -not [string]::IsNullOrWhiteSpace([string]$override.displayNameZh)) { $displayNameZh = [string]$override.displayNameZh }
            if ((Has-Property $override 'categoryKey') -and -not [string]::IsNullOrWhiteSpace([string]$override.categoryKey)) { $categoryKey = [string]$override.categoryKey }
            if ((Has-Property $override 'categoryZh') -and -not [string]::IsNullOrWhiteSpace([string]$override.categoryZh)) { $categoryZh = [string]$override.categoryZh }
            if ((Has-Property $override 'layer') -and -not [string]::IsNullOrWhiteSpace([string]$override.layer)) { $layer = [string]$override.layer }
            if (-not [string]::IsNullOrWhiteSpace($overrideReviewStatus)) { $reviewStatus = $overrideReviewStatus }
            if ((Has-Property $override 'confidence') -and -not [string]::IsNullOrWhiteSpace([string]$override.confidence)) { $confidence = [string]$override.confidence }
            if ((Has-Property $override 'descriptionZh') -and -not [string]::IsNullOrWhiteSpace([string]$override.descriptionZh)) { $descriptionZh = [string]$override.descriptionZh }
            $semanticSource = 'ManualOverride'
        }
        if (Has-Property $override 'manualNoteZh') { $manualNoteZh = [string]$override.manualNoteZh }
        if ($applySemanticOverride -and (Has-Property $override 'loop')) { $loop = [bool]$override.loop }
    }

    $sourceAvailable = $null -ne $rawFile
    $decodeStatus = if ($poseAvailable) { 'PoseReady' }
        elseif ($sourceAvailable) { 'SourceReadyNeedsDecode' }
        else { 'MissingPhysicalHkx' }
    if (-not $sourceAvailable) {
        $descriptionZh += ' TAE 中存在逻辑映射，但当前资源包没有同名物理 HKX。'
    }

    $evidence = New-Object System.Collections.Generic.List[string]
    foreach ($item in $classification.evidence) { $evidence.Add([string]$item) }
    if ($bhvPrimaryRefs.Count -gt 0) {
        $stateEvidence = $bhvPrimaryRefs | ForEach-Object { "状态 $($_.stateIndex)《$($_.officialStateName)》" }
        $evidence.Add('BHV 主动作槽官方映射：' + ($stateEvidence -join '；') + '。')
        $aliasEvidence = @($bhvPrimaryRefs | Where-Object physicalAliasApplied |
            ForEach-Object { "$($_.logicalActionId) → $($_.physicalActionId)" } | Sort-Object -Unique)
        if ($aliasEvidence.Count -gt 0) {
            $evidence.Add('TAE 逻辑动作 ID 到物理 HKX 别名：' + ($aliasEvidence -join '；') + '。')
        }
    }
    elseif ($bhvRefs.Count -gt 0) {
        $evidence.Add("BHV 非主动作槽引用 $($bhvRefs.Count) 条；可用于追踪复用关系，但不单独作为动作语义结论。")
    }
    if ($references.Count -gt 0) {
        $evidence.Add("TAE 引用 $($references.Count) 条；事件类型：$($eventTypes -join '、')。")
    } else {
        $evidence.Add('物理 HKX 存在，但当前 TAE dump 中没有同名映射。')
    }
    $entry = [pscustomobject][ordered]@{
        actionId = $actionId
        physicalActionId = $physicalActionId
        hkxFile = if ($sourceAvailable) { $rawFile.Name } else { '' }
        bank = $classification.bank
        displayNameZh = $displayNameZh
        categoryKey = $categoryKey
        categoryZh = $categoryZh
        descriptionZh = $descriptionZh
        reviewStatus = $reviewStatus
        confidence = $confidence
        semanticSource = $semanticSource
        officialBhvStateRefs = $bhvRefs
        layer = $layer
        loop = $loop
        productionEnabled = ($reviewStatus -in @('OfficialMapped', 'Confirmed') -and $poseAvailable -and $clipCount -ge 4)
        sourceAvailable = $sourceAvailable
        sourceRelativePath = if ($sourceAvailable) { "chr/AC - c0000/$($rawFile.Name)" } else { '' }
        sourceSizeBytes = if ($sourceAvailable) { [int64]$rawFile.Length } else { [int64]0 }
        sourceSha256 = if ($sourceAvailable) { (Get-FileHash -LiteralPath $rawFile.FullName -Algorithm SHA256).Hash } else { '' }
        taeReferenceCount = $references.Count
        taeReferences = $references
        eventTypes = $eventTypes
        poseAvailable = $poseAvailable
        poseJson = if ($poseAvailable) { "Tools/LocalOut/actions/${actionId}_pose.json" } else { '' }
        unityClipCount = $clipCount
        decodeStatus = $decodeStatus
        evidenceZh = $evidence.ToArray()
        manualNoteZh = $manualNoteZh
    }
    $entries.Add($entry)
}

$duplicateIds = @($entries | Group-Object actionId | Where-Object { $_.Count -gt 1 })
if ($duplicateIds.Count -gt 0) {
    throw ('Action catalog contains duplicate ids: ' + (($duplicateIds | ForEach-Object Name) -join ', '))
}

$missingRequiredFields = @($entries | Where-Object {
    [string]::IsNullOrWhiteSpace([string]$_.actionId) -or
    [string]::IsNullOrWhiteSpace([string]$_.displayNameZh) -or
    [string]::IsNullOrWhiteSpace([string]$_.categoryZh) -or
    [string]::IsNullOrWhiteSpace([string]$_.descriptionZh) -or
    [string]::IsNullOrWhiteSpace([string]$_.reviewStatus)
})
if ($missingRequiredFields.Count -gt 0) {
    throw ("Action catalog has $($missingRequiredFields.Count) entries with missing required fields.")
}

$invalidReviewStates = @($entries | Where-Object { $_.reviewStatus -notin @('NeedsReview', 'OfficialMapped', 'Confirmed', 'Rejected') })
if ($invalidReviewStates.Count -gt 0) {
    throw ('Action catalog contains invalid review states: ' + (($invalidReviewStates | ForEach-Object actionId) -join ', '))
}

$orphanLabelIds = @($overridesByAction.Keys | Where-Object { $_ -notin $allActionIds } | Sort-Object)
if ($orphanLabelIds.Count -gt 0) {
    Write-Warning ('Label overrides do not match TAE or physical HKX ids: ' + ($orphanLabelIds -join ', '))
}

$summary = [pscustomobject][ordered]@{
    logicalActionCount = $entries.Count
    physicalActionHkxCount = @($entries | Where-Object sourceAvailable).Count
    taeOnlyActionCount = @($entries | Where-Object { -not $_.sourceAvailable }).Count
    taeRowCount = $taeRowCount
    confirmedCount = @($entries | Where-Object { $_.reviewStatus -eq 'Confirmed' }).Count
    officialMappedCount = @($entries | Where-Object { $_.reviewStatus -eq 'OfficialMapped' }).Count
    needsReviewCount = @($entries | Where-Object { $_.reviewStatus -eq 'NeedsReview' }).Count
    poseReadyCount = @($entries | Where-Object poseAvailable).Count
    unityClipReadyCount = @($entries | Where-Object { $_.unityClipCount -ge 4 }).Count
    productionEnabledCount = @($entries | Where-Object productionEnabled).Count
}

$catalog = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    source = [pscustomobject][ordered]@{
        rawRootHint = 'AC6_ExtractedAssets/chr/AC - c0000'
        taeDump = 'Tools/LocalOut/c0000_tae_dump.json'
        labels = 'Tools/AssetManifests/c0000_action_labels.json'
        bhvStateMap = 'Tools/AssetManifests/c0000_bhv_state_map.json'
        bhvOfficialMetadataAligned = $bhvAlignmentSafe
    }
    summary = $summary
    supportHkxFiles = $supportFiles.ToArray()
    entries = $entries.ToArray()
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($catalogPath, ($catalog | ConvertTo-Json -Depth 12), $utf8NoBom)

$csvRows = $entries | Select-Object @{n='动作ID';e={$_.actionId}}, @{n='中文名称';e={$_.displayNameZh}},
    @{n='分类';e={$_.categoryZh}}, @{n='人工状态';e={$_.reviewStatus}}, @{n='置信度';e={$_.confidence}},
    @{n='语义来源';e={$_.semanticSource}},
    @{n='Bank';e={$_.bank}}, @{n='动画层';e={$_.layer}}, @{n='物理HKX';e={$_.sourceAvailable}},
    @{n='Pose已生成';e={$_.poseAvailable}}, @{n='UnityClip数量';e={$_.unityClipCount}},
    @{n='TAE引用数';e={$_.taeReferenceCount}}, @{n='TAE事件';e={$_.eventTypes -join ';'}},
    @{n='中文说明';e={$_.descriptionZh}}, @{n='人工备注';e={$_.manualNoteZh}}
$csvText = $csvRows | ConvertTo-Csv -NoTypeInformation
[System.IO.File]::WriteAllLines($csvPath, $csvText, $utf8Bom)

$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add('# AC6 动作资产表')
$summaryLines.Add('')
$summaryLines.Add('本表由 `Tools/BuildAC6ActionCatalog.ps1` 自动生成。人工结论只写入 `Tools/AssetManifests/c0000_action_labels.json`，不要直接编辑自动生成 JSON。')
$summaryLines.Add('')
$summaryLines.Add('## 总览')
$summaryLines.Add('')
$summaryLines.Add('| 指标 | 数量 |')
$summaryLines.Add('|---|---:|')
$summaryLines.Add("| TAE 逻辑动作名 | $($summary.logicalActionCount) |")
$summaryLines.Add("| 有物理 HKX 的动作 | $($summary.physicalActionHkxCount) |")
$summaryLines.Add("| 仅 TAE 引用、缺物理 HKX | $($summary.taeOnlyActionCount) |")
$summaryLines.Add("| TAE 状态记录 | $($summary.taeRowCount) |")
$summaryLines.Add("| 已人工确认 | $($summary.confirmedCount) |")
$summaryLines.Add("| BHV 官方状态映射 | $($summary.officialMappedCount) |")
$summaryLines.Add("| 待人工确认 | $($summary.needsReviewCount) |")
$summaryLines.Add("| 已生成 pose JSON | $($summary.poseReadyCount) |")
$summaryLines.Add("| 已生成四部件 Unity Clip | $($summary.unityClipReadyCount) |")
$summaryLines.Add("| 可进入运行时正式清单 | $($summary.productionEnabledCount) |")
$summaryLines.Add('')
$summaryLines.Add('## 使用规则')
$summaryLines.Add('')
$summaryLines.Add('1. `OfficialMapped` 表示动作 ID 已通过 BHV 状态索引和逐状态转换数量双重校验，官方状态名可直接采用。')
$summaryLines.Add('2. `NeedsReview` 表示仍只有宽泛分类；无法被 BHV 主动作槽解释的动作继续保留人工目检流程。')
$summaryLines.Add('3. 人工检查只填写人工备注。只有明确人工裁决时才使用 `Confirmed` 或 `Rejected` 覆盖官方/程序结论。')
$summaryLines.Add('4. `OfficialMapped/Confirmed + PoseReady + 四部件 Clip` 可进入运行时正式清单。')
$summaryLines.Add('5. 完整逐项数据见 `Tools/AssetManifests/c0000_action_catalog.json` 和 UTF-8 CSV。')
$summaryLines.Add('')
$summaryLines.Add('## 分类统计')
$summaryLines.Add('')
$summaryLines.Add('| 分类 | 数量 |')
$summaryLines.Add('|---|---:|')
foreach ($group in $entries | Group-Object categoryZh | Sort-Object Count -Descending) {
    $summaryLines.Add("| $(Escape-Markdown $group.Name) | $($group.Count) |")
}
[System.IO.File]::WriteAllLines($summaryPath, $summaryLines, $utf8NoBom)

$reviewLines = New-Object System.Collections.Generic.List[string]
$reviewLines.Add('# AC6 动作待人工确认')
$reviewLines.Add('')
$reviewLines.Add('以下每一项都没有被当作正式语义。请在 Unity 动作目录窗口中预览并确认，确认结果写入人工覆盖表。')
$reviewLines.Add('')
$reviewLines.Add('| 动作 ID | 中文候选说明 | 分类 | 物理 HKX | Pose | TAE 事件摘要 |')
$reviewLines.Add('|---|---|---|:---:|:---:|---|')
foreach ($entry in $entries | Where-Object { $_.reviewStatus -eq 'NeedsReview' } | Sort-Object bank, actionId) {
    $events = if ($entry.eventTypes.Count -gt 0) { $entry.eventTypes -join '、' } else { '无可识别事件' }
    $reviewLines.Add("| `$($entry.actionId)` | $(Escape-Markdown $entry.displayNameZh) | $(Escape-Markdown $entry.categoryZh) | $($entry.sourceAvailable) | $($entry.poseAvailable) | $(Escape-Markdown $events) |")
}
[System.IO.File]::WriteAllLines($reviewPath, $reviewLines, $utf8NoBom)

Write-Host ("BuildAC6ActionCatalog: logical={0} physical={1} taeOnly={2} confirmed={3} review={4}" -f `
    $summary.logicalActionCount, $summary.physicalActionHkxCount, $summary.taeOnlyActionCount, `
    $summary.confirmedCount, $summary.needsReviewCount)
Write-Host "Catalog: $catalogPath"
Write-Host "CSV:     $csvPath"
Write-Host "Review:  $reviewPath"
