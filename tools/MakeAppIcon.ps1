# Renders the application icon: a two-port USB-CAN interface (two DB9 connectors, status
# LEDs, USB cable). Drawn as vector XAML on a 256x256 canvas and rendered by WPF at each icon
# size; sizes below 48 px use a simplified drawing so the connectors stay readable.
#
# Run with Windows PowerShell (WPF needs an STA thread, which is its default):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\MakeAppIcon.ps1 -IconPath <out.ico> [-PreviewPath <out.png>]
param(
    [Parameter(Mandatory = $true)][string]$IconPath,
    [string]$PreviewPath
)

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

function Get-ConnectorXaml([double]$cx, [bool]$simple) {
    $y0 = 122   # flange top
    $flange = @"
<Rectangle Canvas.Left="$($cx - 50)" Canvas.Top="$y0" Width="100" Height="64" RadiusX="7" RadiusY="7">
  <Rectangle.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
    <GradientStop Offset="0" Color="#F1F4F7"/><GradientStop Offset="1" Color="#A3ADB8"/>
  </LinearGradientBrush></Rectangle.Fill>
</Rectangle>
"@
    # D-shaped shell: wider at the top, as on a real DB9. A thick round-joined stroke in the
    # fill colour rounds its corners.
    $sy = $y0 + 11; $sh = 42
    $shell = "M $($cx - 33),$sy L $($cx + 33),$sy L $($cx + 26),$($sy + $sh) L $($cx - 26),$($sy + $sh) Z"
    $xaml = $flange + @"
<Path Data="$shell" Fill="#5E6873" Stroke="#5E6873" StrokeThickness="12" StrokeLineJoin="Round"/>
<Path Data="$shell" Fill="#1B1F25" Stroke="#1B1F25" StrokeThickness="4" StrokeLineJoin="Round"/>
"@
    if (-not $simple) {
        # Hex standoffs either side, and the 5 + 4 pins.
        foreach ($sx in @(($cx - 42), ($cx + 42))) {
            $xaml += "<Ellipse Canvas.Left='$($sx - 6)' Canvas.Top='$($y0 + 26)' Width='12' Height='12' Fill='#6B7581'/>"
            $xaml += "<Ellipse Canvas.Left='$($sx - 2.5)' Canvas.Top='$($y0 + 29.5)' Width='5' Height='5' Fill='#2A2F36'/>"
        }
        foreach ($px in @(-24, -12, 0, 12, 24)) {
            $xaml += "<Ellipse Canvas.Left='$($cx + $px - 3.6)' Canvas.Top='$($sy + 10)' Width='7.2' Height='7.2' Fill='#F0C14B'/>"
        }
        foreach ($px in @(-18, -6, 6, 18)) {
            $xaml += "<Ellipse Canvas.Left='$($cx + $px - 3.6)' Canvas.Top='$($sy + 27)' Width='7.2' Height='7.2' Fill='#F0C14B'/>"
        }
    }
    return $xaml
}

function Get-IconXaml([bool]$simple) {
    $cable = @"
<Path Data="M 128,74 C 128,36 196,52 222,0" Stroke="#2B3038" StrokeThickness="$(if ($simple) { 16 } else { 12 })" StrokeStartLineCap="Round"/>
<Path Data="M 128,74 C 128,36 196,52 222,0" Stroke="#56606B" StrokeThickness="3" Opacity="0.8"/>
<Rectangle Canvas.Left="114" Canvas.Top="58" Width="28" Height="16" RadiusX="4" RadiusY="4" Fill="#2B3038"/>
"@
    $body = @"
<!-- Top face: a lighter band seen in slight perspective. -->
<Path Data="M 36,64 L 220,64 L 242,100 L 14,100 Z" Stroke="#6E9FD3" StrokeThickness="4" StrokeLineJoin="Round">
  <Path.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
    <GradientStop Offset="0" Color="#7FB0E0"/><GradientStop Offset="1" Color="#5A8EC6"/>
  </LinearGradientBrush></Path.Fill>
</Path>
<!-- Front face. -->
<Path Data="M 14,100 L 242,100 L 242,206 Q 242,224 224,224 L 32,224 Q 14,224 14,206 Z">
  <Path.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
    <GradientStop Offset="0" Color="#2F64A0"/><GradientStop Offset="1" Color="#1C3F6B"/>
  </LinearGradientBrush></Path.Fill>
</Path>
<Rectangle Canvas.Left="14" Canvas.Top="100" Width="228" Height="2.5" Fill="#FFFFFF" Opacity="0.35"/>
"@
    $leds = @"
<Ellipse Canvas.Left="$(if ($simple) { 196 } else { 202 })" Canvas.Top="$(if ($simple) { 104 } else { 107 })" Width="$(if ($simple) { 16 } else { 10 })" Height="$(if ($simple) { 16 } else { 10 })" Fill="#4CFF7A"/>
<Ellipse Canvas.Left="$(if ($simple) { 220 } else { 220 })" Canvas.Top="$(if ($simple) { 104 } else { 107 })" Width="$(if ($simple) { 16 } else { 10 })" Height="$(if ($simple) { 16 } else { 10 })" Fill="#FFB547"/>
"@
    $labels = ""
    if (-not $simple) {
        foreach ($l in @(@(72, "CAN 1"), @(184, "CAN 2"))) {
            $labels += "<TextBlock Canvas.Left='$($l[0] - 50)' Canvas.Top='190' Width='100' TextAlignment='Center' FontFamily='Segoe UI' FontWeight='Bold' FontSize='19' Foreground='#E8F1FB' Text='$($l[1])'/>"
        }
    }

    return @"
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="256" Height="256">
$cable
$body
$(Get-ConnectorXaml 72 $simple)
$(Get-ConnectorXaml 184 $simple)
$leds
$labels
</Canvas>
"@
}

function Render-Png([int]$size, [bool]$simple) {
    $canvas = [System.Windows.Markup.XamlReader]::Parse((Get-IconXaml $simple))
    $box = New-Object System.Windows.Controls.Viewbox
    $box.Child = $canvas
    $box.Width = $size; $box.Height = $size
    $box.Measure((New-Object System.Windows.Size($size, $size)))
    $box.Arrange((New-Object System.Windows.Rect(0, 0, $size, $size)))
    $box.UpdateLayout()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($box)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

# ICO container with PNG-compressed images (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($s in $sizes) { , (Render-Png $s ($s -lt 48)) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($IconPath, $out.ToArray())
"Wrote $IconPath ($($sizes.Count) sizes, $($out.Length) bytes)"

if ($PreviewPath) {
    [System.IO.File]::WriteAllBytes($PreviewPath, (Render-Png 512 $false))
    "Wrote $PreviewPath"
}
