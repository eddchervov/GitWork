$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = Join-Path $PSScriptRoot '..\src\GitWork.App\Assets'
New-Item -ItemType Directory -Path $assetPath -Force | Out-Null
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#19272F'))
$accent = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#80DEC8'))
$pen = [System.Drawing.Pen]::new($accent, 14)
$shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
$pngStream = [System.IO.MemoryStream]::new()

try {
    $shape.AddArc(8, 8, 72, 72, 180, 90)
    $shape.AddArc(176, 8, 72, 72, 270, 90)
    $shape.AddArc(176, 176, 72, 72, 0, 90)
    $shape.AddArc(8, 176, 72, 72, 90, 90)
    $shape.CloseFigure()
    $graphics.FillPath($background, $shape)
    $graphics.DrawLine($pen, 84, 73, 84, 184)
    $graphics.DrawBezier($pen, 84, 160, 84, 113, 174, 153, 174, 73)

    foreach ($center in @(@(84, 68), @(84, 188), @(174, 68))) {
        $graphics.FillEllipse($accent, ($center[0] - 20), ($center[1] - 20), 40, 40)
        $graphics.FillEllipse($background, ($center[0] - 8), ($center[1] - 8), 16, 16)
    }

    $bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $pngStream.ToArray()
    $iconStream = [System.IO.File]::Create((Join-Path $assetPath 'App.ico'))
    $writer = [System.IO.BinaryWriter]::new($iconStream)

    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$pngBytes.Length)
        $writer.Write([uint32]22)
        $writer.Write($pngBytes)
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $pngStream.Dispose()
    $shape.Dispose()
    $pen.Dispose()
    $accent.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
