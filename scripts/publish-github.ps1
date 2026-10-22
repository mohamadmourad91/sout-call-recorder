param([string]$RepositoryName = 'sout-call-recorder')

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
function Invoke-Checked {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed (exit $LASTEXITCODE)." }
}

Push-Location -LiteralPath $projectDirectory
try {
    $identityJson = & gh api user --jq '{login: .login, id: .id}'
    if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub first with: gh auth login --web --git-protocol https' }
    $identity = $identityJson | ConvertFrom-Json
    $repository = "$($identity.login)/$RepositoryName"
    $assets = @('Sout-CallRecorder-v1.2-Windows-x64.zip', 'Sout-Translation-Model-DE-AR-v1.2.zip', 'release-v1.2/SHA256SUMS.txt')
    foreach ($asset in $assets) {
        if (-not (Test-Path -LiteralPath $asset)) { throw "Release asset missing: $asset" }
    }

    $authorName = & git config --get user.name
    if (-not $authorName) { Invoke-Checked git @('config','user.name',$identity.login) }
    $authorEmail = & git config --get user.email
    if (-not $authorEmail) { Invoke-Checked git @('config','user.email',"$($identity.id)+$($identity.login)@users.noreply.github.com") }
    Invoke-Checked git @('add','--','.gitignore','.gitattributes','README.md','BUILDING.md','THIRD_PARTY_NOTICES.md','build_translation_model.py','src','checks','licenses','docs','scripts')
    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 1) { Invoke-Checked git @('commit','-m','Publish Sout 1.2.0 with local German-to-Arabic live translation') }
    elseif ($LASTEXITCODE -ne 0) { throw 'Unable to inspect staged changes.' }

    $origin = & git remote get-url origin 2>$null
    if (-not $origin) {
        Invoke-Checked gh @('repo','create',$repository,'--public','--source','.','--remote','origin','--push','--description','مسجل مكالمات لويندوز بواجهة عربية، مع تحويل الصوت إلى نص وترجمة مباشرة محلية من الألمانية إلى العربية.')
    }
    else {
        if ($origin -notin @("https://github.com/$repository.git", "https://github.com/$repository", "git@github.com:$repository.git")) {
            throw "Origin points to a different repository: $origin"
        }
        Invoke-Checked git @('push','--set-upstream','origin','main')
    }

    & git show-ref --verify --quiet refs/tags/v1.2.0
    if ($LASTEXITCODE -ne 0) { Invoke-Checked git @('tag','v1.2.0') }
    Invoke-Checked git @('push','origin','refs/tags/v1.2.0')

    & gh release view v1.2.0 --repo $repository --json tagName 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Invoke-Checked gh (@('release','create','v1.2.0','--repo',$repository,'--target','main','--title','Sout 1.2.0 — Call Recording & Live German-to-Arabic Translation','--notes-file','docs/release-v1.2.0.md') + $assets)
    }
    else {
        Invoke-Checked gh (@('release','upload','v1.2.0','--repo',$repository) + $assets)
    }
    Invoke-Checked gh @('release','view','v1.2.0','--repo',$repository,'--json','url,assets')
}
finally { Pop-Location }
