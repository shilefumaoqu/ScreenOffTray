param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'Assets'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[void][IO.Directory]::CreateDirectory($OutputDirectory)

function Brush([string]$hex) {
    return New-Object System.Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($hex))
}

function RoundedPath([Drawing.RectangleF]$rect, [float]$radius) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function DrawSoftEllipse([Drawing.Graphics]$graphics, [Drawing.RectangleF]$rect, [string]$color, [int]$layers = 18) {
    for ($i = $layers; $i -ge 1; $i--) {
        $growth = [float]($i * 5)
        $expanded = [Drawing.RectangleF]::Inflate($rect, $growth, $growth)
        $alpha = [Math]::Max(2, [int](18 / $i))
        $c = [Drawing.ColorTranslator]::FromHtml($color)
        $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B))
        try { $graphics.FillEllipse($brush, $expanded) } finally { $brush.Dispose() }
    }
    $core = [Drawing.ColorTranslator]::FromHtml($color)
    $coreBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(34, $core.R, $core.G, $core.B))
    try { $graphics.FillEllipse($coreBrush, $rect) } finally { $coreBrush.Dispose() }
}

function New-Background([string]$path, [bool]$dark) {
    $bitmap = New-Object Drawing.Bitmap 1192, 864, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $rect = New-Object Drawing.Rectangle 0, 0, 1192, 864
        if ($dark) {
            $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.ColorTranslator]::FromHtml('#171A1F')), ([Drawing.ColorTranslator]::FromHtml('#0E1014')), 35.0
            $wash = '#7896B5'
            $line = [Drawing.Color]::FromArgb(24, 255, 255, 255)
        } else {
            $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.ColorTranslator]::FromHtml('#F9FAFC')), ([Drawing.ColorTranslator]::FromHtml('#E4EAF1')), 35.0
            $wash = '#87A8C4'
            $line = [Drawing.Color]::FromArgb(110, 255, 255, 255)
        }
        try { $graphics.FillRectangle($gradient, $rect) } finally { $gradient.Dispose() }

        DrawSoftEllipse $graphics (New-Object Drawing.RectangleF -50, -90, 520, 520) $wash
        DrawSoftEllipse $graphics (New-Object Drawing.RectangleF 830, 310, 440, 440) '#A7BCCB'
        DrawSoftEllipse $graphics (New-Object Drawing.RectangleF 520, -220, 620, 480) '#D8E3EC'

        $bandPath = RoundedPath (New-Object Drawing.RectangleF 610, -110, 270, 1080) 110
        $bandBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($(if ($dark) { 18 } else { 42 }), 255, 255, 255))
        try { $graphics.FillPath($bandBrush, $bandPath) } finally { $bandBrush.Dispose(); $bandPath.Dispose() }

        $pen = New-Object Drawing.Pen $line, 1.2
        try {
            $graphics.DrawLine($pen, 68, 760, 1120, 760)
            $graphics.DrawLine($pen, 730, 52, 1110, 52)
        } finally { $pen.Dispose() }

        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-Side([string]$path, [string]$iconPath, [bool]$dark) {
    $bitmap = New-Object Drawing.Bitmap 480, 918, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $rect = New-Object Drawing.Rectangle 0, 0, 480, 918
        if ($dark) {
            $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.ColorTranslator]::FromHtml('#22262D')), ([Drawing.ColorTranslator]::FromHtml('#111318')), 90.0
            $panel = [Drawing.Color]::FromArgb(48, 255, 255, 255)
            $border = [Drawing.Color]::FromArgb(34, 255, 255, 255)
            $wash = '#6E8CA8'
        } else {
            $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.ColorTranslator]::FromHtml('#F6F8FB')), ([Drawing.ColorTranslator]::FromHtml('#DDE6EF')), 90.0
            $panel = [Drawing.Color]::FromArgb(150, 255, 255, 255)
            $border = [Drawing.Color]::FromArgb(210, 255, 255, 255)
            $wash = '#8CAFC8'
        }
        try { $graphics.FillRectangle($gradient, $rect) } finally { $gradient.Dispose() }

        DrawSoftEllipse $graphics (New-Object Drawing.RectangleF -120, 70, 440, 440) $wash
        DrawSoftEllipse $graphics (New-Object Drawing.RectangleF 180, 610, 390, 390) '#A7BBCB'

        $panelRect = New-Object Drawing.RectangleF 70, 260, 340, 330
        for ($i = 10; $i -ge 1; $i--) {
            $shadowRect = [Drawing.RectangleF]::Inflate($panelRect, $i * 4, $i * 4)
            $shadowPath = RoundedPath $shadowRect (36 + $i)
            $shadow = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(3, 30, 47, 64))
            try { $graphics.FillPath($shadow, $shadowPath) } finally { $shadow.Dispose(); $shadowPath.Dispose() }
        }
        $panelPath = RoundedPath $panelRect 36
        $panelBrush = New-Object Drawing.SolidBrush $panel
        $panelPen = New-Object Drawing.Pen $border, 2
        try { $graphics.FillPath($panelBrush, $panelPath); $graphics.DrawPath($panelPen, $panelPath) } finally {
            $panelBrush.Dispose(); $panelPen.Dispose(); $panelPath.Dispose()
        }

        $icon = [Drawing.Image]::FromFile($iconPath)
        try { $graphics.DrawImage($icon, 150, 290, 180, 180) } finally { $icon.Dispose() }

        $titleFont = New-Object Drawing.Font -ArgumentList @('Segoe UI', [single]24, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
        $subFont = New-Object Drawing.Font -ArgumentList @('Segoe UI', [single]14, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
        $textBrush = New-Object Drawing.SolidBrush ($(if ($dark) { [Drawing.Color]::FromArgb(242,255,255,255) } else { [Drawing.ColorTranslator]::FromHtml('#20242A') }))
        $subBrush = New-Object Drawing.SolidBrush ($(if ($dark) { [Drawing.Color]::FromArgb(170,255,255,255) } else { [Drawing.ColorTranslator]::FromHtml('#6C7480') }))
        try {
            $graphics.DrawString('ScreenOffTray', $titleFont, $textBrush, 90, 520)
            $graphics.DrawString('Display off, work on.', $subFont, $subBrush, 92, 558)
        } finally {
            $titleFont.Dispose(); $subFont.Dispose(); $textBrush.Dispose(); $subBrush.Dispose()
        }

        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-Small([string]$path, [string]$iconPath) {
    $bitmap = New-Object Drawing.Bitmap 128, 128, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        $rect = New-Object Drawing.RectangleF 8, 8, 112, 112
        $pathShape = RoundedPath $rect 28
        $fill = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(205, 244, 248, 252))
        $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(230, 255, 255, 255)), 2
        try { $graphics.FillPath($fill, $pathShape); $graphics.DrawPath($pen, $pathShape) } finally {
            $fill.Dispose(); $pen.Dispose(); $pathShape.Dispose()
        }
        $icon = [Drawing.Image]::FromFile($iconPath)
        try { $graphics.DrawImage($icon, 20, 20, 88, 88) } finally { $icon.Dispose() }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $PSScriptRoot '..\WpfSettings\Assets\app-icon.png'
if (!(Test-Path -LiteralPath $iconPath)) { throw 'Missing application icon source.' }
New-Background (Join-Path $OutputDirectory 'wizard-back-light.png') $false
New-Background (Join-Path $OutputDirectory 'wizard-back-dark.png') $true
New-Side (Join-Path $OutputDirectory 'wizard-side-light.png') $iconPath $false
New-Side (Join-Path $OutputDirectory 'wizard-side-dark.png') $iconPath $true
New-Small (Join-Path $OutputDirectory 'wizard-small.png') $iconPath

Get-ChildItem -LiteralPath $OutputDirectory -Filter 'wizard-*.png' | Select-Object Name, Length
