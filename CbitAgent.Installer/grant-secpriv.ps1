# Grant SeSecurityPrivilege to LocalSystem (S-1-5-18) so the CBIT RMM Agent
# can read the Security event log for monitoring purposes.
$inf = "$env:TEMP\cbit-secpriv.inf"
$sdb = "$env:TEMP\cbit-secpriv.sdb"

try {
    secedit /export /cfg $inf /quiet
    $content = Get-Content $inf

    $found = $false
    $content = $content | ForEach-Object {
        if ($_ -match '^SeSecurityPrivilege') {
            $found = $true
            if ($_ -notmatch '\*S-1-5-18') {
                $_ + ',*S-1-5-18'
            } else {
                $_
            }
        } else {
            $_
        }
    }

    if (-not $found) {
        $idx = [array]::IndexOf($content, '[Privilege Rights]')
        if ($idx -ge 0) {
            $content = @($content[0..$idx]) + @('SeSecurityPrivilege = *S-1-5-18') + @($content[($idx+1)..($content.Length-1)])
        }
    }

    Set-Content $inf $content
    secedit /configure /db $sdb /cfg $inf /areas USER_RIGHTS /quiet
} finally {
    Remove-Item $inf, $sdb -ErrorAction SilentlyContinue
}
