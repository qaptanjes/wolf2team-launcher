<# =============================================================================
   WOLFTEAM PRIVATE-SERVER LAUNCHER  (PowerShell / WinForms - no installs)
   -----------------------------------------------------------------------------
   Runs on any Windows machine. No Python, no compiling. Double-click the
   accompanying  WolfTeamLauncher.bat  to start it.

   Uses your recovered art as the window background (the red PLAY button is
   already part of that image; we just put an invisible click-region over it).

   >>> Edit the CONFIG block below; everything else can stay as-is. <<<
   ============================================================================= #>

# ============================ CONFIG =========================================
$LoginUrl  = "http://127.0.0.1/api/login.php"  # XAMPP serves htdocs\api\login.php
# Client folder: set WT_CLIENT_DIR to override (the client is kept outside the repo / OneDrive).
$ClientDir = if ($env:WT_CLIENT_DIR) { $env:WT_CLIENT_DIR } else { "C:\WolfTeamClient\WolfTeamClient" }
$GameExe   = Join-Path $ClientDir "WolfTeam.bin"                    # path to the game binary
# ---- DEBUG launch mode -------------------------------------------------------
# ============================ LAUNCH MODE ====================================
# ROOT-CAUSE FIX (Ghidra-confirmed): the game's main() checks argv[1].
#   _!!DEBUG!!_ <ip> <port> ...    -> DEBUG path -> invalidates the lobby session ->
#                                     the subObj wipe -> the server list NEVER renders.
#   <arg1> <hexToken> <num> <num>  -> NORMAL path -> VALID session -> list RENDERS.
# So we launch in NORMAL MODE. The PHP login below STILL RUNS (it works) and gives us the
# real {token}; that token becomes the hexToken. The game reads the broker addr from
# wolf.xfs (NOT the command line in normal mode), so ensure wolf.xfs broker -> 127.0.0.1.
#
# Normal-mode arg shape (from WolfTeam.bin FUN_00425f50):
#     "<game>"  <arg1>  <hexToken>  <num1>  <num2>
# -----------------------------------------------------------------------------
$BrokerIp   = "127.0.0.1"   # (kept for reference; broker addr now comes from wolf.xfs)
$BrokerPort = "40706"
$Num1       = "1"           # trailing %d  (placeholder id - tune if the game rejects)
$Num2       = "0"           # trailing %u  (placeholder id)
# NORMAL-mode args (NO _!!DEBUG!!_). {token} = the real session token from login.php below.
# If login.php's token isn't a clean hex string, see $TokenFallback note further down.
$GameArgs  = @("{user}", "{token}", $Num1, $Num2)
# --- old DEBUG mode (this CAUSED the subObj wipe - do NOT use):
# $GameArgs  = @("_!!DEBUG!!_", $BrokerIp, $BrokerPort, "{user}", "{token}")
$BgImage   = Join-Path $ClientDir "selectgame_frame_US.bmp"      # the launcher art (client file, not in git)

# Where the red PLAY button sits inside the image (nudge if your art differs):
$PlayX = 583; $PlayY = 582; $PlayW = 178; $PlayH = 62

# News / welcome text (edit freely):
$NewsTitle = "WELCOME TO THE PACK"
$NewsBody  = @"
Private server is ONLINE.

Enter your account below and hit PLAY.
No account? Register on the site or ask an admin.

Good hunting.
"@
# =============================================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# ---- load background art ----
if (-not (Test-Path $BgImage)) {
    [System.Windows.Forms.MessageBox]::Show("Background image not found:`n$BgImage`n`nPut selectgame_frame_US.bmp next to this launcher.","WolfTeam Launcher")
    return
}
$bg = [System.Drawing.Bitmap]::FromFile($BgImage)

# ---- form ----
$form = New-Object System.Windows.Forms.Form
$form.Text = "WolfTeam"
$form.FormBorderStyle = 'None'
$form.StartPosition = 'CenterScreen'
$form.ClientSize = New-Object System.Drawing.Size($bg.Width, $bg.Height)
$form.BackgroundImage = $bg
$form.BackgroundImageLayout = 'None'
$form.KeyPreview = $true

# drag the borderless window by clicking the background
$script:drag=$false; $script:dx=0; $script:dy=0
$form.Add_MouseDown({ $script:drag=$true; $script:dx=$_.X; $script:dy=$_.Y })
$form.Add_MouseUp(  { $script:drag=$false })
$form.Add_MouseMove({ if($script:drag){ $form.Left += ($_.X - $script:dx); $form.Top += ($_.Y - $script:dy) } })
# Esc closes
$form.Add_KeyDown({ if($_.KeyCode -eq 'Escape'){ $form.Close() } })

$BLACK = [System.Drawing.Color]::Black
$WHITE = [System.Drawing.Color]::White
$RED   = [System.Drawing.Color]::FromArgb(210,40,40)
$GRAY  = [System.Drawing.Color]::FromArgb(150,155,160)
$FIELD = [System.Drawing.Color]::FromArgb(21,21,26)

function New-Label($text,$x,$y,$w,$h,$size,$bold,$color){
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text
    $l.Location = New-Object System.Drawing.Point($x,$y)
    $l.Size = New-Object System.Drawing.Size($w,$h)
    $style = if($bold){[System.Drawing.FontStyle]::Bold}else{[System.Drawing.FontStyle]::Regular}
    $l.Font = New-Object System.Drawing.Font("Arial",$size,$style)
    $l.ForeColor = $color
    $l.BackColor = $BLACK     # matches the image's black centre = seamless
    $form.Controls.Add($l)
    return $l
}

# ---- news / welcome (over the black centre) ----
(New-Label $NewsTitle 95 135 500 30 15 $true $RED) | Out-Null
(New-Label $NewsBody  95 178 560 220 11 $false $WHITE) | Out-Null

# ---- login fields ----
(New-Label "USERNAME" 95 430 200 20 10 $true $GRAY) | Out-Null
$tbUser = New-Object System.Windows.Forms.TextBox
$tbUser.Location = New-Object System.Drawing.Point(95,453)
$tbUser.Size = New-Object System.Drawing.Size(240,26)
$tbUser.BackColor = $FIELD; $tbUser.ForeColor = $WHITE
$tbUser.BorderStyle = 'FixedSingle'
$tbUser.Font = New-Object System.Drawing.Font("Arial",11)
$form.Controls.Add($tbUser)

(New-Label "PASSWORD" 95 490 200 20 10 $true $GRAY) | Out-Null
$tbPass = New-Object System.Windows.Forms.TextBox
$tbPass.Location = New-Object System.Drawing.Point(95,513)
$tbPass.Size = New-Object System.Drawing.Size(240,26)
$tbPass.BackColor = $FIELD; $tbPass.ForeColor = $WHITE
$tbPass.BorderStyle = 'FixedSingle'
$tbPass.UseSystemPasswordChar = $true
$tbPass.Font = New-Object System.Drawing.Font("Arial",11)
$form.Controls.Add($tbPass)

# ---- status line ----
$status = New-Label "Ready." 95 560 480 22 9 $false $GRAY

# ---- PLAY click-region (over the baked-in red button) ----
$rect = New-Object System.Drawing.Rectangle($PlayX,$PlayY,$PlayW,$PlayH)
$crop = $bg.Clone($rect, $bg.PixelFormat)      # pixel-perfect copy of the button
$play = New-Object System.Windows.Forms.PictureBox
$play.Image = $crop
$play.Location = New-Object System.Drawing.Point($PlayX,$PlayY)
$play.Size = New-Object System.Drawing.Size($PlayW,$PlayH)
$play.SizeMode = 'Normal'
$play.Cursor = 'Hand'
$form.Controls.Add($play)

# ---- the login + launch action ----
$DoLogin = {
    $u = $tbUser.Text.Trim(); $p = $tbPass.Text
    if (-not $u -or -not $p) { $status.Text = "Enter a username and password."; $status.ForeColor = $RED; return }
    $status.Text = "Logging in..."; $status.ForeColor = $WHITE
    [System.Windows.Forms.Application]::DoEvents()
    try {
        $body = "user=$([uri]::EscapeDataString($u))&pass=$([uri]::EscapeDataString($p))"
        $resp = Invoke-RestMethod -Uri $LoginUrl -Method Post -Body $body `
                  -ContentType "application/x-www-form-urlencoded" -TimeoutSec 8 `
                  -Headers @{ "User-Agent" = "WolfLoader/1.0" }
    } catch {
        $status.Text = "Login failed: $($_.Exception.Message)"; $status.ForeColor = $RED; return
    }
    if (-not $resp.ok) {
        $msg = if ($resp.error) { $resp.error } else { "Invalid login." }
        $status.Text = $msg; $status.ForeColor = $RED; return
    }
    $ip = "$($resp.ip)"; $port = "$($resp.port)"; $token = "$($resp.token)"
    # Normal-mode hexToken must be a hex string. If login.php returned a non-hex token,
    # fall back to a placeholder hex (the broker is ours, so it accepts any session).
    if ([string]::IsNullOrWhiteSpace($token) -or ($token -cnotmatch '^[0-9A-Fa-f]+$')) {
        $token = "DEADBEEFDEADBEEFDEADBEEFDEADBEEF"
    }
    $status.Text = "Login OK. Launching game..."; $status.ForeColor = [System.Drawing.Color]::FromArgb(80,200,80)
    [System.Windows.Forms.Application]::DoEvents()

    if (-not (Test-Path $GameExe)) { $status.Text = "Game not found: $GameExe"; $status.ForeColor = $RED; return }
    $argList = @()
    foreach ($a in $GameArgs) {
        $argList += ($a -replace '\{user\}',$u -replace '\{ip\}',$ip -replace '\{port\}',$port -replace '\{token\}',$token)
    }
    try {
        # Resolve to the FULL path and run it as a raw executable. Using the absolute
        # path (not just "WolfTeam.bin") stops Windows from treating .bin as an unknown
        # file type and showing the "How do you want to open this file?" dialog.
        $exeFull = (Resolve-Path $GameExe).Path
        $wd = Split-Path -Parent $exeFull
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exeFull
        $psi.Arguments = ($argList -join ' ')
        $psi.WorkingDirectory = $wd
        $psi.UseShellExecute = $false   # run directly, bypass file-association lookup
        [System.Diagnostics.Process]::Start($psi) | Out-Null
        Start-Sleep -Milliseconds 400
        $form.Close()
    } catch {
        $status.Text = "Launch failed: $($_.Exception.Message)"; $status.ForeColor = $RED
    }
}

$play.Add_Click($DoLogin)
$tbPass.Add_KeyDown({ if ($_.KeyCode -eq 'Enter') { & $DoLogin } })
$tbUser.Add_KeyDown({ if ($_.KeyCode -eq 'Enter') { $tbPass.Focus() } })

$tbUser.Select()
[void]$form.ShowDialog()
