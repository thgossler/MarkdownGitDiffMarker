#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Confirm-RevertAction {
    $caption = "Revert last commit locally and push to 'origin'"
    $message = @(
        "This will execute the following commands:",
        "  git revert HEAD --no-edit",
        "  git push origin HEAD",
        "",
        "Note: This will create a new commit that undoes the last commit.",
        "The original last commit will remain in Git history (it is not deleted).",
        "",
        "Do you want to continue?"
    ) -join [Environment]::NewLine

    $choices = [System.Management.Automation.Host.ChoiceDescription[]]@(
        (New-Object System.Management.Automation.Host.ChoiceDescription '&Yes','Proceed with revert and push'),
        (New-Object System.Management.Automation.Host.ChoiceDescription '&No','Cancel')
    )

    $defaultChoice = 1 # No
    $result = $Host.UI.PromptForChoice($caption, $message, $choices, $defaultChoice)
    return ($result -eq 0)
}

function Assert-GitAvailable {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Git is not installed or not available in PATH."
    }
}

function Assert-InGitRepository {
    & git rev-parse --is-inside-work-tree *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "The current directory is not inside a Git repository."
    }
}

try {
    Assert-GitAvailable
    Assert-InGitRepository

    if (-not (Confirm-RevertAction)) {
        Write-Host "Aborted by user."
        exit 0
    }

    Write-Host "Reverting last commit locally..."
    & git revert HEAD --no-edit
    $revertExit = $LASTEXITCODE
    if ($revertExit -ne 0) {
        Write-Error "git revert failed with exit code $revertExit. If there are conflicts, resolve them and commit, or run 'git revert --abort' to cancel."
        exit $revertExit
    }

    Write-Host "Pushing changes to remote 'origin'..."
    & git push origin HEAD
    $pushExit = $LASTEXITCODE
    if ($pushExit -ne 0) {
        Write-Error "git push failed with exit code $pushExit. The local revert succeeded, but the push did not. Fix any issues and push manually."
        exit $pushExit
    }

    Write-Host "Revert completed locally and pushed to 'origin'."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
