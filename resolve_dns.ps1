$domains = @("orchids.app", "slelguoygbfzlpylpxfs.supabase.co", "posthog.com")
$results = @()
foreach ($d in $domains) {
    try {
        $ips = [System.Net.Dns]::GetHostAddresses($d) | Where-Object { $_.AddressFamily -eq "InterNetwork" } | Select-Object -ExpandProperty IPAddressToString
        foreach ($ip in $ips) {
            $results += "$d=$ip"
        }
    } catch {
        $results += "$d=ERROR"
    }
}
$results | Out-File -FilePath "$PSScriptRoot\dns_results.txt" -Encoding utf8
