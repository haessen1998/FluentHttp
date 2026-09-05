param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../publish/nugets')
)

$ErrorActionPreference = 'Stop'
$repositoryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projects = Get-ChildItem -Path (Join-Path $repositoryPath 'src/*/*.csproj'), (Join-Path $repositoryPath 'extensions/*/*.csproj')
foreach ($project in $projects) {
    & dotnet pack $project.FullName --configuration $Configuration --no-build --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Packing failed: $($project.Name)" }
}
