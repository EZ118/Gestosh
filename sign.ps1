# Signs dist\Gestosh.exe and dist\Gestosh.Debug.exe with an Authenticode
# certificate.
#
# SmartScreen only stops warning about a download once the file carries a
# signature from a publicly trusted code-signing certificate AND that certificate
# has built up reputation (an EV certificate starts with reputation). Metadata
# and a self-signed certificate cannot satisfy SmartScreen for other people;
# -SelfSigned exists only for local pipeline testing and for machines where you
# deploy the certificate yourself.
#
# Examples:
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -Thumbprint 0123456789ABCDEF...
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -Pfx .\EZ118.pfx -PfxPassword secret
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -SelfSigned
param(
    [string]$Pfx,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [switch]$SelfSigned,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$targets = @('Gestosh.exe', 'Gestosh.Debug.exe') |
    ForEach-Object { Join-Path $dist $_ } |
    Where-Object { Test-Path $_ }
if (-not $targets) {
    throw 'Nothing to sign. Run build.ps1 first.'
}

# Used when New-SelfSignedCertificate is unavailable (for example when the
# certificate store cannot be written). The certificate lives only in memory and
# is gone when the script exits.
$localCertSource = @'
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class GestoshLocalCert
{
    public static X509Certificate2 Create(string subject)
    {
        RSA rsa = RSA.Create(3072);
        CertificateRequest request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        OidCollection eku = new OidCollection();
        eku.Add(new Oid("1.3.6.1.5.5.7.3.3"));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
    }
}
'@

function New-LocalSigningCertificate {
    try {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject 'CN=EZ118, O=EZ118, C=CN' `
            -KeyUsage DigitalSignature `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(5)
        Write-Host ("Self-signed certificate stored in Cert:\CurrentUser\My ({0})." -f $certificate.Thumbprint)
        return $certificate
    }
    catch {
        Write-Warning ("Cannot write to the certificate store ({0}). Using an in-memory certificate instead." -f $_.Exception.Message)
        if (-not ('GestoshLocalCert' -as [type])) { Add-Type -TypeDefinition $localCertSource }
        $certificate = [GestoshLocalCert]::Create('CN=EZ118, O=EZ118, C=CN')
        Write-Host ("In-memory certificate {0} (not persisted; import the exported .cer to trust it)." -f $certificate.Thumbprint)
        return $certificate
    }
}

function Get-CodeSigningCertificate {
    if ($Thumbprint) {
        $normalized = $Thumbprint -replace '[^0-9A-Fa-f]', ''
        $found = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $normalized }
        if (-not $found) { throw "No certificate with thumbprint $normalized in CurrentUser\My or LocalMachine\My." }
        return @($found)[0]
    }

    if ($Pfx) {
        $path = (Resolve-Path -LiteralPath $Pfx).Path
        $secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($path, $secure)
    }

    if ($SelfSigned) {
        return New-LocalSigningCertificate
    }

    $candidates = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }
    if (-not $candidates) {
        throw @'
No code-signing certificate with a private key was found in Cert:\CurrentUser\My.

Options:
  - Buy an OV/EV code-signing certificate (the only thing that removes SmartScreen
    for other people), import it, then run:  sign.ps1 -Thumbprint <thumbprint>
  - Import a .pfx and run:                   sign.ps1 -Pfx .\cert.pfx -PfxPassword ...
  - Create a throwaway local certificate:    sign.ps1 -SelfSigned
'@
    }
    if (@($candidates).Count -gt 1) {
        Write-Host 'Several code-signing certificates are available:'
        $candidates | Select-Object Thumbprint, Subject, NotAfter | Format-Table -AutoSize
        throw 'Pass -Thumbprint to choose one.'
    }
    return @($candidates)[0]
}

$certificate = Get-CodeSigningCertificate
if (-not $certificate.HasPrivateKey) {
    throw 'The selected certificate has no private key; signing is impossible.'
}
Write-Host ("Signing certificate: {0}" -f $certificate.Subject)
Write-Host ("Thumbprint:          {0}" -f $certificate.Thumbprint)
Write-Host ("Valid until:         {0:yyyy-MM-dd}" -f $certificate.NotAfter)

$signArgs = @{
    Certificate   = $certificate
    HashAlgorithm = 'SHA256'
    ErrorAction   = 'Stop'
}
if (-not $NoTimestamp) { $signArgs['TimestampServer'] = $TimestampUrl }

$failed = 0
foreach ($file in $targets) {
    $name = Split-Path -Leaf $file
    Write-Host ("Signing {0}..." -f $name)
    try {
        $signature = Set-AuthenticodeSignature -FilePath $file @signArgs
    }
    catch {
        Write-Warning ("Signature failed for {0}: {1}" -f $name, $_.Exception.Message)
        $failed++
        continue
    }

    $verified = Get-AuthenticodeSignature -FilePath $file
    if ($null -eq $verified.SignerCertificate) {
        Write-Warning ("{0}: no signer certificate after signing ({1})" -f $name, $verified.Status)
        $failed++
        continue
    }

    Write-Host ("  {0}: {1}" -f $name, $verified.Status)
    Write-Host ("  signer: {0}" -f $verified.SignerCertificate.Subject)
    if ($verified.TimeStamperCertificate) {
        Write-Host ("  timestamp: {0}" -f $verified.TimeStamperCertificate.Subject)
    }
    else {
        Write-Host '  timestamp: none (the signature expires together with the certificate)'
    }

    if ($verified.Status -ne 'Valid') {
        if ($SelfSigned) {
            Write-Warning '  The signature is written, but the test certificate is not trusted, so SmartScreen still blocks it.'
            Write-Warning ("  Import {0} into Trusted Root and Trusted Publishers on this machine to make Windows accept it." -f 'dist\Gestosh-selfsigned.cer')
        }
        else {
            Write-Warning '  Windows does not consider this signature valid. Check that the certificate chains to a trusted root.'
            $failed++
        }
    }
}

if ($SelfSigned) {
    $cer = Join-Path $dist 'Gestosh-selfsigned.cer'
    [System.IO.File]::WriteAllBytes($cer, $certificate.Export('Cert'))
    Write-Host ''
    Write-Host "Public certificate exported to: $cer"
    Write-Host 'Trust it on THIS machine (current user) with:'
    Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\CurrentUser\Root"
    Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\CurrentUser\TrustedPublisher"
    Write-Host ''
    Write-Host 'A self-signed certificate does NOT satisfy SmartScreen on other machines.'
    Write-Host 'Only a certificate from a public CA (EV recommended) plus reputation does.'
}

if ($failed -ne 0) { throw "$failed file(s) could not be signed." }
Write-Host ''
Write-Host 'Signing finished.'
