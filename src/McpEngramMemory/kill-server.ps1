Write-Host "Searching for stuck McpEngramMemory processes..."

# 1. Kill any process named exactly McpEngramMemory
$namedProcesses = Get-Process -Name McpEngramMemory -ErrorAction SilentlyContinue
if ($namedProcesses) {
    Write-Host "Found McpEngramMemory native processes, killing..."
    $namedProcesses | Stop-Process -Force
}

# 2. Kill any dotnet process that is running the McpEngramMemory dll
# Get-CimInstance is preferred over Get-WmiObject in newer PowerShell versions
$dotnetProcesses = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' AND CommandLine LIKE '%McpEngramMemory%'" -ErrorAction SilentlyContinue
if ($dotnetProcesses) {
    Write-Host "Found dotnet processes running McpEngramMemory, killing..."
    foreach ($proc in $dotnetProcesses) {
        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Server termination complete."
