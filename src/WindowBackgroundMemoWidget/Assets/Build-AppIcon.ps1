<#
name         : Build-AppIcon.ps1
desc         : Dracula 메모·핀 벡터 마스터와 투명 다중 크기 ICO를 함께 재생성한다.
author       : NotedexOrig3
create date  : 2026-10-24
update date  : 2026-10-24 NotedexOrig3 : SVG 마스터에서 동일 도형을 PNG 프레임과 ICO로 제작.
#>
param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$colors = @{
    Background = [System.Drawing.ColorTranslator]::FromHtml('#282A36')
    Edge       = [System.Drawing.ColorTranslator]::FromHtml('#44475A')
    Paper      = [System.Drawing.ColorTranslator]::FromHtml('#F8F8F2')
    Purple     = [System.Drawing.ColorTranslator]::FromHtml('#BD93F9')
    Pin        = [System.Drawing.ColorTranslator]::FromHtml('#FF79C6')
    Ink        = [System.Drawing.ColorTranslator]::FromHtml('#44475A')
}

<#
name         : DrawRoundRectangle
desc         : 벡터처럼 정의된 64 DIP 정사각형 아이콘의 모서리를 같은 반경으로 채운다.
author       : NotedexOrig3
create date  : 2026-10-24
update date  : 2026-10-24 NotedexOrig3 : Dracula 배경판과 접힌 메모 도형 생성.
#>
function Draw-RoundRectangle([System.Drawing.Graphics]$Graphics, [System.Drawing.Brush]$Brush, [System.Drawing.Pen]$Pen,
    [single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $Graphics.FillPath($Brush, $path)
    if ($null -ne $Pen) { $Graphics.DrawPath($Pen, $path) }
    $path.Dispose()
}

<#
name         : Draw-WindowMemoMark
desc         : 배경·접힌 메모·본문 줄·핀을 고정된 64 DIP 기준으로 그려 작은 프레임에서도 비율을 유지한다.
author       : NotedexOrig3
create date  : 2026-10-24
update date  : 2026-10-24 NotedexOrig3 : 단순한 메모·데스크톱 핀 앱 기호 구성.
#>
function Draw-WindowMemoMark([System.Drawing.Graphics]$Graphics, [single]$Scale) {
    $Graphics.ScaleTransform($Scale, $Scale)
    $tile = [System.Drawing.SolidBrush]::new($colors.Background)
    $tileEdge = [System.Drawing.Pen]::new($colors.Edge, 1.4)
    $paper = [System.Drawing.SolidBrush]::new($colors.Paper)
    $paperEdge = [System.Drawing.Pen]::new($colors.Purple, 1.3)
    $fold = [System.Drawing.SolidBrush]::new($colors.Purple)
    $ink = [System.Drawing.Pen]::new($colors.Ink, 2.3)
    $pin = [System.Drawing.Pen]::new($colors.Pin, 2.4)
    $pinHead = [System.Drawing.SolidBrush]::new($colors.Pin)

    try {
        $ink.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $ink.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pin.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pin.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        # 어두운 정사각형을 창 제목 표시줄·트레이의 어두운 배경에서도 식별한다.
        Draw-RoundRectangle $Graphics $tile $tileEdge 3 3 58 58 14
        # 세 줄과 접힌 모서리로 동작을 축소 화면에서도 구별한다.
        Draw-RoundRectangle $Graphics $paper $paperEdge 16 10 32 44 3
        $foldPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $foldPath.AddPolygon([System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(39.7, 10.5),
            [System.Drawing.PointF]::new(47, 17.3),
            [System.Drawing.PointF]::new(39.7, 17.3)))
        $Graphics.FillPath($fold, $foldPath)
        $foldPath.Dispose()
        $Graphics.DrawLine($paperEdge, 39.4, 10.4, 39.4, 17.7)
        $Graphics.DrawLine($paperEdge, 39.4, 17.7, 47, 17.7)
        # 핀은 고정된 바탕화면 메모를, 세 줄은 내용을 나타낸다.
        $Graphics.DrawLine($pin, 44.5, 21.5, 40.3, 27.5)
        $Graphics.FillEllipse($pinHead, 42.1, 13.6, 6, 6)
        $Graphics.DrawLine($ink, 22, 32, 39, 32)
        $Graphics.DrawLine($ink, 22, 38.5, 42, 38.5)
        $Graphics.DrawLine($ink, 22, 45, 35, 45)
    }
    finally {
        foreach ($resource in @($tile, $tileEdge, $paper, $paperEdge, $fold, $ink, $pin, $pinHead)) {
            $resource.Dispose()
        }
    }
}

$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-labelledby="title desc">
  <title id="title">Window Background Memo Widget</title>
  <desc id="desc">Dracula midnight desktop with a folded page, short text lines, and a pink pin.</desc>
  <rect x="3.7" y="3.7" width="56.6" height="56.6" rx="14" fill="#282A36" stroke="#44475A" stroke-width="1.4"/>
  <rect x="16.65" y="10.65" width="30.7" height="42.7" rx="3" fill="#F8F8F2" stroke="#BD93F9" stroke-width="1.3"/>
  <path d="M39.7 10.5 47 17.3h-7.3z" fill="#BD93F9"/>
  <path d="M39.4 10.4v7.3h7.6" fill="none" stroke="#BD93F9" stroke-width="1.3" stroke-linejoin="round"/>
  <path d="m44.5 21.5-4.2 6" fill="none" stroke="#FF79C6" stroke-width="2.4" stroke-linecap="round"/>
  <circle cx="45.1" cy="16.6" r="3" fill="#FF79C6"/>
  <path d="M22 32h17M22 38.5h20M22 45h13" fill="none" stroke="#44475A" stroke-width="2.3" stroke-linecap="round"/>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $outputDirectory 'WindowMemo.svg'), $svg + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$images = [Collections.Generic.List[object]]::new()
try {
    foreach ($size in $iconSizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $bitmap.SetResolution(96, 96)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            Draw-WindowMemoMark $graphics ($size / 64.0)
        }
        finally { $graphics.Dispose() }

        $png = [IO.MemoryStream]::new()
        $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($size -eq 256) {
            $preview = [IO.File]::Create((Join-Path $outputDirectory 'WindowMemo-preview.png'))
            try { $bitmap.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png) }
            finally { $preview.Dispose() }
        }
        $images.Add([pscustomobject]@{ Size = $size; Data = $png.ToArray() })
        $png.Dispose()
        $bitmap.Dispose()
    }

    # ICO 디렉터리의 오프셋과 PNG 엔트리를 명시해 Windows가 원래 크기를 고를 수 있게 한다.
    $iconPath = Join-Path $outputDirectory 'WindowMemo.ico'
    $stream = [IO.File]::Create($iconPath)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$images.Count)
        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -eq 256) { [byte]0 } else { [byte]$image.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$image.Data.Length)
            $writer.Write([UInt32]$offset)
            $offset += $image.Data.Length
        }
        foreach ($image in $images) { $writer.Write($image.Data) }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}
finally {
    foreach ($image in $images) { if ($null -ne $image.Data) { $image.Data = $null } }
}
Write-Output "Created SVG, 9-frame ICO, and 256 px PNG in $outputDirectory"
