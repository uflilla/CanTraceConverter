# Renders the application icon: a two-port USB-CAN interface seen in three-quarter view - a
# matte black housing with a label and status LEDs on top, two DB9 connectors on the front
# and the USB cable leaving at the back. Drawn as vector XAML on a 256x256 canvas and
# rendered by WPF at each icon size; sizes below 48 px use a simplified drawing so the shape
# stays readable.
#
# Run with Windows PowerShell (WPF needs an STA thread, which is its default):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\MakeAppIcon.ps1 -IconPath <out.ico> [-PreviewPath <out.png>] [-SheetPath <out.png>]
param(
    [Parameter(Mandatory = $true)][string]$IconPath,
    [string]$PreviewPath,
    [string]$SheetPath
)

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# Box geometry. The front face is seen straight on; the top and right faces recede by $dx,$dy.
$fx = 18; $fw = 178        # front face left edge and width
$ft = 146; $fh = 62        # front face top edge and height
$dx = 42; $dy = -92        # depth vector (front edge -> back edge)
$fr = $fx + $fw; $fb = $ft + $fh

function Get-ConnectorXaml([double]$cx, [double]$cy, [bool]$simple) {
    $xaml = @"
<Rectangle Canvas.Left="$($cx - 34)" Canvas.Top="$($cy - 21)" Width="68" Height="42" RadiusX="4" RadiusY="4">
  <Rectangle.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
    <GradientStop Offset="0" Color="#D9DEE3"/><GradientStop Offset="0.5" Color="#AEB6BF"/><GradientStop Offset="1" Color="#7D8792"/>
  </LinearGradientBrush></Rectangle.Fill>
</Rectangle>
"@
    # D-shaped shell, wider at the top; a thick round-joined stroke rounds its corners.
    $shell = "M $($cx - 21),$($cy - 11) L $($cx + 21),$($cy - 11) L $($cx + 16),$($cy + 11) L $($cx - 16),$($cy + 11) Z"
    $xaml += @"
<Path Data="$shell" Fill="#59626C" Stroke="#59626C" StrokeThickness="8" StrokeLineJoin="Round"/>
<Path Data="$shell" Fill="#0E1013" Stroke="#0E1013" StrokeThickness="3" StrokeLineJoin="Round"/>
"@
    if (-not $simple) {
        foreach ($sx in @(($cx - 28), ($cx + 28))) {
            $xaml += "<Ellipse Canvas.Left='$($sx - 4)' Canvas.Top='$($cy - 4)' Width='8' Height='8' Fill='#4A525B'/>"
            $xaml += "<Ellipse Canvas.Left='$($sx - 1.6)' Canvas.Top='$($cy - 1.6)' Width='3.2' Height='3.2' Fill='#1A1D21'/>"
        }
        foreach ($px in @(-14, -7, 0, 7, 14)) {
            $xaml += "<Ellipse Canvas.Left='$($cx + $px - 2.2)' Canvas.Top='$($cy - 7)' Width='4.4' Height='4.4' Fill='#E9BC4E'/>"
        }
        foreach ($px in @(-10.5, -3.5, 3.5, 10.5)) {
            $xaml += "<Ellipse Canvas.Left='$($cx + $px - 2.2)' Canvas.Top='$($cy + 2.5)' Width='4.4' Height='4.4' Fill='#E9BC4E'/>"
        }
    }
    return $xaml
}

function Get-IconXaml([bool]$simple) {
    # Top face corners: front-left, front-right, back-right, back-left.
    $top = "M $fx,$ft L $fr,$ft L $($fr + $dx),$($ft + $dy) L $($fx + $dx),$($ft + $dy) Z"
    $side = "M $fr,$ft L $($fr + $dx),$($ft + $dy) L $($fr + $dx),$($fb + $dy) L $fr,$fb Z"
    $front = "M $fx,$ft L $fr,$ft L $fr,$fb L $fx,$fb Z"

    # Content drawn on the top face: local x runs along the front edge, local y from the back
    # edge (0) to the front edge (100). The matrix maps that rectangle onto the face.
    $m21 = -$dx / 100.0; $m22 = -$dy / 100.0
    $ox = $fx + $dx; $oy = $ft + $dy

    $cableWidth = if ($simple) { 15 } else { 11 }
    $labelText = if ($simple) { "" } else {
        @"
<TextBlock Canvas.Left="34" Canvas.Top="20" Width="110" TextAlignment="Center" FontFamily="Segoe UI" FontWeight="Bold" FontSize="30" Foreground="#20252B" Text="UCAN"/>
<TextBlock Canvas.Left="34" Canvas.Top="54" Width="110" TextAlignment="Center" FontFamily="Segoe UI" FontWeight="SemiBold" FontSize="12" Foreground="#3B434C" Text="2 x CAN  &#183;  USB"/>
"@
    }
    $ledSize = if ($simple) { 22 } else { 11 }
    $ledTop = if ($simple) { 70 } else { 82 }

    return @"
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="256" Height="256">
  <!-- Soft contact shadow. -->
  <Ellipse Canvas.Left="6" Canvas.Top="$($fb - 16)" Width="250" Height="40">
    <Ellipse.Fill><RadialGradientBrush>
      <GradientStop Offset="0" Color="#70000000"/><GradientStop Offset="1" Color="#00000000"/>
    </RadialGradientBrush></Ellipse.Fill>
  </Ellipse>

  <!-- USB cable leaving the back of the housing. -->
  <Path Data="M 128,$($ft + $dy + 10) C 124,24 70,30 30,0" Stroke="#1B1D21" StrokeThickness="$cableWidth" StrokeStartLineCap="Round"/>
  <Path Data="M 128,$($ft + $dy + 10) C 124,24 70,30 30,0" Stroke="#4C525A" StrokeThickness="2.5" Opacity="0.7"/>
  <Rectangle Canvas.Left="114" Canvas.Top="$($ft + $dy - 6)" Width="28" Height="20" RadiusX="5" RadiusY="5" Fill="#1B1D21"/>

  <!-- Housing: right side, top and front faces, edges softened by a round-joined stroke. -->
  <Path Data="$side" Fill="#0F1012" Stroke="#0F1012" StrokeThickness="6" StrokeLineJoin="Round"/>
  <Path Data="$top" Stroke="#2B2F34" StrokeThickness="6" StrokeLineJoin="Round">
    <Path.Fill><LinearGradientBrush StartPoint="0,1" EndPoint="1,0">
      <GradientStop Offset="0" Color="#2A2D32"/><GradientStop Offset="0.55" Color="#3C4047"/><GradientStop Offset="1" Color="#24272B"/>
    </LinearGradientBrush></Path.Fill>
  </Path>
  <Path Data="$front" Stroke="#141619" StrokeThickness="6" StrokeLineJoin="Round">
    <Path.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
      <GradientStop Offset="0" Color="#1D1F23"/><GradientStop Offset="1" Color="#0B0C0E"/>
    </LinearGradientBrush></Path.Fill>
  </Path>

  <!-- Edge highlights where the faces meet. -->
  <Line X1="$fx" Y1="$ft" X2="$fr" Y2="$ft" Stroke="#8A9099" StrokeThickness="2" Opacity="0.8"/>
  <Line X1="$fr" Y1="$ft" X2="$($fr + $dx)" Y2="$($ft + $dy)" Stroke="#6B717A" StrokeThickness="2" Opacity="0.8"/>
  <!-- Rim light on the far edges, so the dark housing stays visible on a dark taskbar. -->
  <Line X1="$($fx + $dx)" Y1="$($ft + $dy)" X2="$($fr + $dx)" Y2="$($ft + $dy)" Stroke="#5F656E" StrokeThickness="2.5"/>
  <Line X1="$($fr + $dx)" Y1="$($ft + $dy)" X2="$($fr + $dx)" Y2="$($fb + $dy)" Stroke="#4E545C" StrokeThickness="2.5"/>
  <Line X1="$($fx + $dx)" Y1="$($ft + $dy)" X2="$fx" Y2="$ft" Stroke="#4E545C" StrokeThickness="2"/>

  <!-- Top-face content: glossy streak, label and LEDs. -->
  <Canvas>
    <Canvas.RenderTransform><MatrixTransform Matrix="1,0,$m21,$m22,$ox,$oy"/></Canvas.RenderTransform>
    <Rectangle Canvas.Left="0" Canvas.Top="0" Width="$fw" Height="100" Opacity="0.18">
      <Rectangle.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="1,0.4">
        <GradientStop Offset="0.25" Color="#00FFFFFF"/><GradientStop Offset="0.45" Color="#FFFFFFFF"/><GradientStop Offset="0.6" Color="#00FFFFFF"/>
      </LinearGradientBrush></Rectangle.Fill>
    </Rectangle>
    <Rectangle Canvas.Left="30" Canvas.Top="14" Width="118" Height="$(if ($simple) { 48 } else { 62 })" RadiusX="6" RadiusY="6">
      <Rectangle.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
        <GradientStop Offset="0" Color="#E4E8EC"/><GradientStop Offset="1" Color="#A9B1BA"/>
      </LinearGradientBrush></Rectangle.Fill>
    </Rectangle>
    $labelText
    <Ellipse Canvas.Left="$(if ($simple) { 112 } else { 128 })" Canvas.Top="$ledTop" Width="$ledSize" Height="$ledSize" Fill="#3DFF6E"/>
    <Ellipse Canvas.Left="$(if ($simple) { 140 } else { 146 })" Canvas.Top="$ledTop" Width="$ledSize" Height="$ledSize" Fill="#FF4A3D"/>
  </Canvas>

  <!-- Two DB9 connectors on the front face. -->
  $(Get-ConnectorXaml ($fx + 50) ($ft + $fh / 2) $simple)
  $(Get-ConnectorXaml ($fx + 128) ($ft + $fh / 2) $simple)
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

if ($SheetPath) {
    # Every icon size at 1:1 on a light and a dark strip, to judge the small sizes.
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawRectangle([System.Windows.Media.Brushes]::WhiteSmoke, $null, (New-Object System.Windows.Rect(0, 0, 820, 280)))
    $dc.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(32, 32, 32))), $null, (New-Object System.Windows.Rect(0, 140, 820, 140)))
    $x = 8
    for ($i = 0; $i -lt $sizes.Count - 1; $i++) {
        $src = New-Object System.Windows.Media.Imaging.BitmapImage
        $src.BeginInit(); $src.StreamSource = New-Object System.IO.MemoryStream(, $images[$i]); $src.EndInit()
        $s = $sizes[$i]
        $dc.DrawImage($src, (New-Object System.Windows.Rect($x, 8, $s, $s)))
        $dc.DrawImage($src, (New-Object System.Windows.Rect($x, 148, $s, $s)))
        $x += $s + 10
    }
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(820, 280, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [System.IO.File]::Create($SheetPath); $enc.Save($fs); $fs.Close()
    "Wrote $SheetPath"
}
