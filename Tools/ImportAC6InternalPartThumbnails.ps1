param(
    [string]$AtlasPath = "Tools/LocalOut/AC6InternalParts/Menu/SB_PartsThumbnails.png",
    [string]$DestinationRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/InternalParts/Thumbnails",
    [string]$TextureMetaTemplate = "Assets/TutorialInfo/Icons/URP.png.meta",
    [string]$GeneratedPartRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6Parts"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $AtlasPath)) {
    throw "AC6 parts thumbnail atlas was not found: $AtlasPath"
}
if (-not (Test-Path -LiteralPath $TextureMetaTemplate)) {
    throw "Unity texture metadata template was not found: $TextureMetaTemplate"
}

Add-Type -AssemblyName System.Drawing.Common
New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

$parts = @(
    [pscustomobject]@{ Id = 8960; X = 4128; Y = 1168 },
    [pscustomobject]@{ Id = 8961; X = 4644; Y = 1168 },
    [pscustomobject]@{ Id = 8962; X = 5160; Y = 1168 },
    [pscustomobject]@{ Id = 8963; X = 5676; Y = 1168 },
    [pscustomobject]@{ Id = 8964; X = 6192; Y = 1168 },
    [pscustomobject]@{ Id = 8965; X = 6708; Y = 1168 },
    [pscustomobject]@{ Id = 8966; X = 7224; Y = 1168 },
    [pscustomobject]@{ Id = 8967; X = 1032; Y = 1460 },
    [pscustomobject]@{ Id = 8968; X = 1548; Y = 1460 },
    [pscustomobject]@{ Id = 8969; X = 2064; Y = 1460 },
    [pscustomobject]@{ Id = 8980; X = 2580; Y = 1460 },
    [pscustomobject]@{ Id = 8981; X = 3096; Y = 1460 },
    [pscustomobject]@{ Id = 8982; X = 3612; Y = 1460 },
    [pscustomobject]@{ Id = 8983; X = 4128; Y = 1460 },
    [pscustomobject]@{ Id = 8984; X = 4644; Y = 1460 },
    [pscustomobject]@{ Id = 8985; X = 5160; Y = 1460 },
    [pscustomobject]@{ Id = 8986; X = 5676; Y = 1460 },
    [pscustomobject]@{ Id = 8987; X = 6192; Y = 1460 },
    [pscustomobject]@{ Id = 8988; X = 6708; Y = 1460 },
    [pscustomobject]@{ Id = 8989; X = 7224; Y = 1460 },
    [pscustomobject]@{ Id = 8990; X = 1032; Y = 1752 },
    [pscustomobject]@{ Id = 8991; X = 1032; Y = 2044 },
    [pscustomobject]@{ Id = 8992; X = 1032; Y = 2336 },
    [pscustomobject]@{ Id = 8993; X = 1032; Y = 2628 }
)

$atlas = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $AtlasPath))
$metaTemplate = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $TextureMetaTemplate))
$guidByIcon = @{}
try {
    foreach ($part in $parts) {
        $crop = [System.Drawing.Bitmap]::new(
            512,
            288,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($crop)
            try {
                $graphics.DrawImage(
                    $atlas,
                    [System.Drawing.Rectangle]::new(0, 0, 512, 288),
                    [System.Drawing.Rectangle]::new($part.X, $part.Y, 512, 288),
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            $outputPath = Join-Path $DestinationRoot "WP_A_$($part.Id).png"
            $crop.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)

            $guidSeed = [System.Text.Encoding]::UTF8.GetBytes("AC6InternalPartThumbnail:$($part.Id)")
            $hash = [System.Security.Cryptography.MD5]::HashData($guidSeed)
            $guid = ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
            $guidByIcon[$part.Id] = $guid
            $metaText = [System.Text.RegularExpressions.Regex]::Replace(
                $metaTemplate,
                "(?m)^guid: .*$",
                "guid: $guid")
            [System.IO.File]::WriteAllText(
                ([System.IO.Path]::GetFullPath("$outputPath.meta")),
                $metaText)
        }
        finally {
            $crop.Dispose()
        }
    }
}
finally {
    $atlas.Dispose()
}

if (Test-Path -LiteralPath $GeneratedPartRoot) {
    Get-ChildItem -LiteralPath $GeneratedPartRoot -File |
        Where-Object { $_.Name -match '^AC6_(FCS|GEN)_\d+\.asset$' } |
        ForEach-Object {
            $assetText = [System.IO.File]::ReadAllText($_.FullName)
            if ($assetText -notmatch '(?m)^    sourceIconId: (\d+)$') {
                return
            }

            $iconId = [int]$Matches[1]
            if (-not $guidByIcon.ContainsKey($iconId)) {
                return
            }

            $reference = "  previewImage: {fileID: 2800000, guid: $($guidByIcon[$iconId]), type: 3}"
            $assetText = [System.Text.RegularExpressions.Regex]::Replace(
                $assetText,
                '(?m)^  previewImage: .*$',
                $reference)
            [System.IO.File]::WriteAllText($_.FullName, $assetText)
        }
}

Write-Host "Imported $($parts.Count) AC6 internal-part thumbnails into $DestinationRoot"
