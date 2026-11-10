param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Target
)

Add-Type -AssemblyName System.Drawing
$sourceImage = [System.Drawing.Image]::FromFile($Source)
try {
    $sizes = @(16,20,24,32,40,48,64,128,256)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $scale = [Math]::Min($size / $sourceImage.Width, $size / $sourceImage.Height)
                $width = [int][Math]::Round($sourceImage.Width * $scale)
                $height = [int][Math]::Round($sourceImage.Height * $scale)
                $graphics.DrawImage($sourceImage, [int](($size-$width)/2), [int](($size-$height)/2), $width, $height)
            } finally { $graphics.Dispose() }
            $memory = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                $images.Add($memory.ToArray())
            } finally { $memory.Dispose() }
        } finally { $bitmap.Dispose() }
    }
    $stream = [System.IO.File]::Create($Target)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$sizes.Count)
            $offset = 6 + 16 * $sizes.Count
            for ($index=0; $index -lt $sizes.Count; $index++) {
                $size = $sizes[$index]
                $writer.Write([byte]($size -band 255))
                $writer.Write([byte]($size -band 255))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$images[$index].Length)
                $writer.Write([uint32]$offset)
                $offset += $images[$index].Length
            }
            foreach ($bytes in $images) { $writer.Write($bytes) }
        } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
} finally { $sourceImage.Dispose() }
