# Functions only. Classification is explicit; version resources are observations,
# not evidence that a process is unrelated to the OCR execution boundary.
function Assert-OcrReviewedDesktopEntry($Entry) {
    $path=Assert-OcrPlainLocalPath $Entry.Path
    $name=[IO.Path]::GetFileName($path)
    if ((Test-OcrExecutionBoundary $path $name '') -or
        $name -match '^(?i:dotnet|powershell|pwsh|cmd|node|rundll32|mshta|wscript|cscript)\.exe$') {
        throw 'document-ocr-maintenance-desktop-runtime-exclusion-forbidden'
    }
    if ($Entry.Sha256 -cnotmatch '^[A-F0-9]{64}$' -or !$Entry.DesktopIdentity -or !$Entry.Reason -or !$Entry.Evidence) {
        throw 'document-ocr-maintenance-desktop-classification-incomplete'
    }
    if ((Get-FileHash -LiteralPath $path).Hash -cne $Entry.Sha256) { throw 'document-ocr-maintenance-desktop-executable-changed' }
    $evidence=$Entry.Evidence
    switch -CaseSensitive ($evidence.Kind) {
        'Authenticode' {
            $signature=Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
            if ($signature.Status -ne 'Valid' -or !$evidence.SignerSubject -or !$evidence.SignerThumbprint -or
                $signature.SignerCertificate.Subject -cne $evidence.SignerSubject -or
                $signature.SignerCertificate.Thumbprint -cne $evidence.SignerThumbprint) {
                throw 'document-ocr-maintenance-desktop-signer-unproven'
            }
        }
        'AppxPackage' {
            if (!$evidence.Name -or !$evidence.PackageFullName -or !$evidence.Publisher -or !$evidence.InstallLocation -or
                $evidence.SignatureKind -cnotin @('Store','System')) { throw 'document-ocr-maintenance-desktop-package-unproven' }
            $packages=@(Get-AppxPackage -Name $evidence.Name -ErrorAction Stop | Where-Object PackageFullName -CEQ $evidence.PackageFullName)
            if ($packages.Count -ne 1) { throw 'document-ocr-maintenance-desktop-package-unproven' }
            $package=$packages[0]
            $installation=Assert-OcrPlainLocalPath $package.InstallLocation
            if ($package.Status -ne 'Ok' -or [string]$package.SignatureKind -cne $evidence.SignatureKind -or
                $package.Publisher -cne $evidence.Publisher -or $installation -ine $evidence.InstallLocation -or
                !$path.StartsWith($installation.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
                throw 'document-ocr-maintenance-desktop-package-unproven'
            }
        }
        default { throw 'document-ocr-maintenance-desktop-evidence-unsupported' }
    }
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    [ordered]@{Path=$path;Sha256=$Entry.Sha256;BoundaryClass='UnrelatedDesktopExecutable';
        DesktopIdentity=$Entry.DesktopIdentity;Reason=$Entry.Reason;Evidence=$evidence;
        ObservedVersion=[ordered]@{ProductName=$version.ProductName;CompanyName=$version.CompanyName}}
}

function Read-OcrReviewedDesktopClassifications([string]$Path) {
    [void](Assert-OcrPlainLocalPath $Path)
    $bytes=[IO.File]::ReadAllBytes($Path)
    $packet=[Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable
    if ($packet.Version -ne 1 -or !@($packet.Entries).Count) { throw 'document-ocr-maintenance-desktop-classifications-required' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $entries=@(foreach ($entry in $packet.Entries) {
        $canonical=Assert-OcrPlainLocalPath $entry.Path
        if (!$seen.Add($canonical)) { throw 'document-ocr-maintenance-desktop-classification-duplicate' }
        Assert-OcrReviewedDesktopEntry $entry
    })
    [ordered]@{InputSha256=(Get-OcrBytesHash $bytes);Entries=$entries}
}
