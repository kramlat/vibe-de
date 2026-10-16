# VDE terminal integration for PowerShell.
#
# vterminal loads this when it starts PowerShell (set VDE_TERMINAL_INTEGRATION=1 to load it by hand: put
#   . /usr/share/vde/terminal/vde-integration.ps1
# in your $PROFILE). It tells the terminal, with standard escape sequences, where the prompt is, where each
# command ended and with what result, and which folder you are in, so vterminal can jump between prompts, mark
# failed commands and open new windows in the same folder. It changes nothing else: your own prompt is kept.
#
#   OSC 133;A  prompt starts      OSC 133;B  prompt ends, you type   OSC 133;D;n  the last command ended with n
#   OSC 7      current folder (file://host/path)

if ($env:VDE_TERMINAL_INTEGRATION -ne '1' -or $global:__VdeIntegrationLoaded) { return }
$global:__VdeIntegrationLoaded = $true
$global:__VdeOriginalPrompt = $function:prompt
$global:__VdeLastHistoryId = -1
$global:__VdeFirstPrompt = $true

function global:__VdeEscape([string]$path) {
    # percent-encode what a URI cannot hold, keep the slashes
    $sb = [System.Text.StringBuilder]::new()
    foreach ($b in [System.Text.Encoding]::UTF8.GetBytes($path)) {
        $c = [char]$b
        if (($c -match '[A-Za-z0-9/._~-]')) { [void]$sb.Append($c) } else { [void]$sb.AppendFormat('%{0:X2}', $b) }
    }
    $sb.ToString()
}

function global:prompt {
    $success = $?
    $exit = $global:LASTEXITCODE
    $esc = [char]27
    $bel = [char]7
    $out = ''

    $last = Get-History -Count 1
    $ranCommand = ($null -ne $last) -and ($last.Id -ne $global:__VdeLastHistoryId)
    if (-not $global:__VdeFirstPrompt) {
        $code = 0
        if ($ranCommand) {
            if (-not $success) { $code = if ($null -ne $exit -and $exit -ne 0) { $exit } else { 1 } }
            $out += "$esc]133;D;$code$bel"
        } else {
            $out += "$esc]133;D$bel"   # an empty line: no command, no result
        }
    }
    $global:__VdeFirstPrompt = $false
    if ($null -ne $last) { $global:__VdeLastHistoryId = $last.Id }

    $location = Get-Location
    if ($location.Provider.Name -eq 'FileSystem') {
        $hostName = [System.Net.Dns]::GetHostName()
        $out += "$esc]7;file://$hostName$(__VdeEscape $location.ProviderPath)$bel"
    }

    $out += "$esc]133;A$bel"
    $text = & $global:__VdeOriginalPrompt
    $out + $text + "$esc]133;B$bel"
}
