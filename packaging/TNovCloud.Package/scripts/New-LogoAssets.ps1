[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function New-TNovCloudLogo {
    param(
        [Parameter(Mandatory)][int] $Size,
        [Parameter(Mandatory)][string] $Path
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $blue = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 15, 108, 189))
        $white = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::White)
        try {
            $graphics.FillEllipse($blue, 0, 0, $Size - 1, $Size - 1)

            $cloud = [System.Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $cloud.AddEllipse($Size * 0.16, $Size * 0.42, $Size * 0.38, $Size * 0.34)
                $cloud.AddEllipse($Size * 0.34, $Size * 0.24, $Size * 0.43, $Size * 0.48)
                $cloud.AddEllipse($Size * 0.59, $Size * 0.39, $Size * 0.28, $Size * 0.35)
                $cloud.AddRectangle(
                    [System.Drawing.RectangleF]::new(
                        $Size * 0.26, $Size * 0.49, $Size * 0.50, $Size * 0.27))
                $graphics.FillPath($white, $cloud)
            }
            finally {
                $cloud.Dispose()
            }

            $font = [System.Drawing.Font]::new(
                'Segoe UI', [Math]::Max(7, $Size * 0.22),
                [System.Drawing.FontStyle]::Bold,
                [System.Drawing.GraphicsUnit]::Pixel)
            try {
                $format = [System.Drawing.StringFormat]::new()
                $format.Alignment = [System.Drawing.StringAlignment]::Center
                $format.LineAlignment = [System.Drawing.StringAlignment]::Center
                $graphics.DrawString(
                    'T', $font, $blue,
                    [System.Drawing.RectangleF]::new(
                        0, $Size * 0.34, $Size, $Size * 0.46),
                    $format)
                $format.Dispose()
            }
            finally {
                $font.Dispose()
            }
        }
        finally {
            $blue.Dispose()
            $white.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-TNovCloudLogo -Size 44 -Path (Join-Path $OutputDirectory 'Square44x44Logo.png')
New-TNovCloudLogo -Size 150 -Path (Join-Path $OutputDirectory 'Square150x150Logo.png')
New-TNovCloudLogo -Size 50 -Path (Join-Path $OutputDirectory 'StoreLogo.png')
