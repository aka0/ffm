Add-Type -AssemblyName System.Drawing
$designRoot = $PSScriptRoot
$previewNames = @('original', 'beard')
$encodedImages = @{}
foreach ($previewName in $previewNames) {
    $portraitSource = Join-Path $designRoot "demo-$previewName.png"
    $portraitImage = [System.Drawing.Image]::FromFile($portraitSource)
    $portraitThumb = New-Object System.Drawing.Bitmap(480, 480)
    $portraitGraphics = [System.Drawing.Graphics]::FromImage($portraitThumb)
    $portraitGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $portraitGraphics.DrawImage($portraitImage, 0, 0, 480, 480)
    $portraitDestination = Join-Path $designRoot "demo-$previewName-preview.jpg"
    $portraitThumb.Save($portraitDestination, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $portraitGraphics.Dispose()
    $portraitThumb.Dispose()
    $portraitImage.Dispose()
    $encodedImages[$previewName] = 'data:image/jpeg;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($portraitDestination))
}
$conceptSource = Join-Path $designRoot 'app-mockup.html'
$conceptMarkup = [IO.File]::ReadAllText($conceptSource)
$conceptMarkup = $conceptMarkup.Replace('__ORIGINAL__', $encodedImages['original']).Replace('__BEARD__', $encodedImages['beard'])
$inlinePath = 'C:\Users\aka0\.codex\visualizations\2026\11\11\01a1cc1c-d41c-7b41-a197-9b99185b5477\facepack-manager-ui.html'
[IO.File]::WriteAllText($inlinePath, $conceptMarkup)
$standalonePath = Join-Path $designRoot 'preview.html'
$standaloneMarkup = '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>FM Facepack Manager - UI Concept</title><style>body{margin:24px auto;max-width:1120px;padding:0 12px;background:#e9edef}@media(prefers-color-scheme:dark){body{background:#0c1116}}</style></head><body>' + $conceptMarkup + '</body></html>'
[IO.File]::WriteAllText($standalonePath, $standaloneMarkup)
Get-Item -LiteralPath $inlinePath, $standalonePath | Select-Object FullName, Length
