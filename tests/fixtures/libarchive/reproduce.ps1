# Recreate pinned upstream test fixtures; requires HTTPS access to raw.githubusercontent.com.
$ErrorActionPreference = 'Stop'
$revision = 'c719b9b1f56621d92063a85361cc8d114f5575a9'
$base = "https://raw.githubusercontent.com/libarchive/libarchive/$revision"
$destination = $PSScriptRoot
$names = @(1..8 | ForEach-Object { 'test_read_format_rar5_multiarchive.part{0:00}.rar.uu' -f $_ }) + @('test_read_format_rar5_compressed.rar.uu', 'test_read_format_rar4_solid_encrypted.rar.uu', 'test_read_format_rar4_encrypted_filenames.rar.uu', 'test_read_format_rar5_solid_encrypted.rar.uu', 'test_read_format_rar5_encrypted_filenames.rar.uu')
$records = @()
foreach ($name in ($names + @('COPYING','test_read_format_rar5.c','test_read_format_rar_encryption.c','test_read_format_rar_encryption_data.c','test_read_format_rar_encryption_header.c','test_read_format_rar.c'))) {
    $remotePath = if ($name -eq 'COPYING') { $name } else { "libarchive/test/$name" }
    $url = "$base/$remotePath"
    $target = Join-Path $destination $name
    & curl.exe --fail --silent --show-error --location $url --output $target
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    $record = [ordered]@{ SourceUrl = $url; SourceFile = $name; SourceSha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
    if ($name.EndsWith('.uu')) {
        $bytes = [System.Collections.Generic.List[byte]]::new()
        $started = $false
        foreach ($line in [System.IO.File]::ReadAllLines($target)) {
            if ($line.StartsWith('begin ')) { $started = $true; continue }
            if (-not $started) { continue }
            if ($line -eq 'end') { break }
            if ($line.Length -eq 0) { continue }
            $count = ([int][char]$line[0] - 32) -band 63
            $decoded = [System.Collections.Generic.List[byte]]::new()
            for ($i = 1; $i + 3 -lt $line.Length; $i += 4) {
                $a = ([int][char]$line[$i] - 32) -band 63
                $b = ([int][char]$line[$i+1] - 32) -band 63
                $c = ([int][char]$line[$i+2] - 32) -band 63
                $d = ([int][char]$line[$i+3] - 32) -band 63
                $decoded.Add([byte](($a -shl 2) -bor ($b -shr 4)))
                $decoded.Add([byte]((($b -band 15) -shl 4) -bor ($c -shr 2)))
                $decoded.Add([byte]((($c -band 3) -shl 6) -bor $d))
            }
            if ($decoded.Count -lt $count) { throw "Malformed uuencode in $name" }
            for ($i = 0; $i -lt $count; $i++) { $bytes.Add($decoded[$i]) }
        }
        if (-not $started -or $bytes.Count -eq 0) { throw "Missing uuencode payload in $name" }
        $decodedName = $name.Substring(0, $name.Length - 3)
        $decodedPath = Join-Path $destination $decodedName
        [System.IO.File]::WriteAllBytes($decodedPath, $bytes.ToArray())
        $record.DecodedFile = $decodedName
        $record.DecodedBytes = $bytes.Count
        $record.DecodedSha256 = (Get-FileHash -LiteralPath $decodedPath -Algorithm SHA256).Hash
    }
    $records += $record
}
[ordered]@{ Repository = 'https://github.com/libarchive/libarchive'; Revision = $revision; Files = $records } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
