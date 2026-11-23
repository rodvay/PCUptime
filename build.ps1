$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

Add-Type -AssemblyName System.Drawing

function New-AppIcon {
    param([string]$OutPath)

    $sizes = @(16, 32, 48, 256)
    $pngList = New-Object System.Collections.Generic.List[byte[]]

    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $pad = [float]($sz * 0.06)
        $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($sz - $pad * 2), ($sz - $pad * 2))
        $rad = [float]($sz * 0.22)
        $d = $rad * 2.0

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 22, 30, 42),
            [System.Drawing.Color]::FromArgb(255, 11, 15, 22),
            45.0
        )
        $g.FillPath($bgBrush, $path)

        $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 48, 62, 82), [Math]::Max(1.0, $sz * 0.035))
        $g.DrawPath($borderPen, $path)

        # Outer emerald ring arc
        $ringPad = [float]($sz * 0.22)
        $ringRect = New-Object System.Drawing.RectangleF($ringPad, $ringPad, ($sz - $ringPad * 2), ($sz - $ringPad * 2))
        $trackPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 52, 211, 153), [Math]::Max(1.5, $sz * 0.085))
        $g.DrawEllipse($trackPen, $ringRect)

        $arcPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 52, 211, 153), [Math]::Max(1.5, $sz * 0.085))
        $arcPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arcPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($arcPen, $ringRect, -90, 260)

        # Warm amber lightning bolt inside center
        $cx = [float]($sz * 0.50)
        $cy = [float]($sz * 0.50)
        $bw = [float]($sz * 0.26)
        $bh = [float]($sz * 0.34)
        $lx = $cx - $bw * 0.5
        $ly = $cy - $bh * 0.5

        $bolt = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(($lx + $bw * 0.60), $ly)),
            (New-Object System.Drawing.PointF(($lx + $bw * 0.08), ($ly + $bh * 0.54))),
            (New-Object System.Drawing.PointF(($lx + $bw * 0.48), ($ly + $bh * 0.54))),
            (New-Object System.Drawing.PointF(($lx + $bw * 0.35), ($ly + $bh))),
            (New-Object System.Drawing.PointF(($lx + $bw * 0.92), ($ly + $bh * 0.44))),
            (New-Object System.Drawing.PointF(($lx + $bw * 0.52), ($ly + $bh * 0.44)))
        )
        $boltBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 251, 191, 36))
        $g.FillPolygon($boltBrush, $bolt)

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngList.Add($ms.ToArray())

        $ms.Dispose()
        $boltBrush.Dispose()
        $arcPen.Dispose()
        $trackPen.Dispose()
        $borderPen.Dispose()
        $bgBrush.Dispose()
        $path.Dispose()
        $g.Dispose()
        $bmp.Dispose()
    }

    # Write standard multi-image ICO (PNG frames)
    $fs = [System.IO.File]::Create($OutPath)
    $bwOut = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICONDIR
        $bwOut.Write([uint16]0)
        $bwOut.Write([uint16]1)
        $bwOut.Write([uint16]$sizes.Count)

        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $sz = $sizes[$i]
            $bytes = $pngList[$i]
            $dimByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
            $bwOut.Write($dimByte)            # width
            $bwOut.Write($dimByte)            # height
            $bwOut.Write([byte]0)             # color count
            $bwOut.Write([byte]0)             # reserved
            $bwOut.Write([uint16]1)           # planes
            $bwOut.Write([uint16]32)          # bit count
            $bwOut.Write([uint32]$bytes.Length)
            $bwOut.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        foreach ($bytes in $pngList) {
            $bwOut.Write($bytes)
        }
    }
    finally {
        $bwOut.Dispose()
        $fs.Dispose()
    }
}

Write-Host "Generating app.ico..."
New-AppIcon -OutPath (Join-Path $scriptDir "app.ico")

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe bulunamadi: $csc"
}

Write-Host "Compiling PCUptime.exe..."
& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /codepage:65001 `
    /utf8output `
    /win32icon:"$scriptDir\app.ico" `
    /win32manifest:"$scriptDir\app.manifest" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll `
    /out:"$scriptDir\PCUptime.exe" `
    "$scriptDir\Program.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Derleme basarisiz oldu (ExitCode=$LASTEXITCODE)"
}

$exeInfo = Get-Item (Join-Path $scriptDir "PCUptime.exe")
Write-Host ("Basariyla olusturuldu: {0} ({1:N1} KB)" -f $exeInfo.FullName, ($exeInfo.Length / 1024.0))
