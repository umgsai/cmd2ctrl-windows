# Convert icon.png into a multi-size app.ico for csc /win32icon (Windows built-in .NET Framework only)
# NOTE: keep this file ASCII-only. PowerShell 5 parses scripts without BOM as ANSI/GBK and
# multi-byte characters in comments can swallow the following line.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$pngPath = Join-Path $root 'icon.png'
$icoPath = Join-Path $root 'app.ico'
if (-not (Test-Path $pngPath)) { throw "Missing icon file: $pngPath" }

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48)
$source = [System.Drawing.Image]::FromFile($pngPath)
$bitmaps = @()
try
{
    foreach ($size in $sizes)
    {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try
        {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($source, 0, 0, $size, $size)
        }
        finally
        {
            $g.Dispose()
        }
        $bitmaps += $bmp
    }
}
finally
{
    $source.Dispose()
}

function Get-IconImageBytes([System.Drawing.Bitmap]$bmp)
{
    $w = $bmp.Width
    $h = $bmp.Height
    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $andSize = $maskStride * $h

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    try
    {
        # BITMAPINFOHEADER: height must be doubled (XOR bitmap + AND mask)
        $bw.Write([UInt32]40)
        $bw.Write([Int32]$w)
        $bw.Write([Int32]($h * 2))
        $bw.Write([UInt16]1)
        $bw.Write([UInt16]32)
        $bw.Write([UInt32]0)
        $bw.Write([UInt32]($w * $h * 4 + $andSize))
        $bw.Write([Int32]0)
        $bw.Write([Int32]0)
        $bw.Write([UInt32]0)
        $bw.Write([UInt32]0)

        $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try
        {
            $buffer = New-Object byte[] ($data.Stride * $h)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)
            $stride = $data.Stride
        }
        finally
        {
            $bmp.UnlockBits($data)
        }

        # ICO pixel rows are stored bottom-up
        for ($y = $h - 1; $y -ge 0; $y--)
        {
            $bw.Write($buffer, $y * $stride, $w * 4)
        }

        # AND mask: 32bpp uses the alpha channel, mask stays all zero
        $bw.Write((New-Object byte[] $andSize))
        $bw.Flush()
        return ,$ms.ToArray()
    }
    finally
    {
        $bw.Dispose()
        $ms.Dispose()
    }
}

$images = @()
foreach ($bmp in $bitmaps)
{
    $images += ,[pscustomobject]@{
        Size = [int]$bmp.Width
        Data = [byte[]](Get-IconImageBytes $bmp)
    }
    $bmp.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
try
{
    $bw.Write([UInt16]0)                # reserved
    $bw.Write([UInt16]1)                # type = icon
    $bw.Write([UInt16]$images.Count)    # image count

    $offset = 6 + 16 * $images.Count
    foreach ($img in $images)
    {
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $bw.Write([byte]$dim)
        $bw.Write([byte]$dim)
        $bw.Write([byte]0)              # palette size
        $bw.Write([byte]0)              # reserved
        $bw.Write([UInt16]1)            # planes
        $bw.Write([UInt16]32)           # bits per pixel
        $bw.Write([UInt32]$img.Data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $img.Data.Length
    }

    foreach ($img in $images)
    {
        $bw.Write([byte[]]$img.Data)
    }

    $bw.Flush()
    $bytes = $ms.ToArray()
}
finally
{
    $bw.Dispose()
    $ms.Dispose()
}

try
{
    [System.IO.File]::WriteAllBytes($icoPath, $bytes)
}
catch
{
    Write-Host "Write failed: $icoPath - $($_.Exception.Message)"
    exit 1
}

Write-Host "Generated: $icoPath ($($sizes -join '/') px)"
