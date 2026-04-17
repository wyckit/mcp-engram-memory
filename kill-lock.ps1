#!/usr/bin/env pwsh
# kill-lock.ps1 -- Kill processes locking the MCP server DLL
#
# Usage:
#   pwsh kill-lock.ps1                        # kills anything locking McpEngramMemory.Core.dll
#   pwsh kill-lock.ps1 -File path\to\any.dll  # kills anything locking the specified file
#   pwsh kill-lock.ps1 -Force                 # skip confirmation prompt

param(
    [string] $File  = "",
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Default: the DLL that gets locked when the MCP server is running
if (-not $File) {
    $File = Join-Path $PSScriptRoot "src\McpEngramMemory\bin\Release\net8.0\McpEngramMemory.Core.dll"
}

if (-not (Test-Path $File)) {
    Write-Host "  File not found: $File" -ForegroundColor Yellow
    Write-Host "  (Nothing to unlock)" -ForegroundColor DarkGray
    exit 0
}

$File = (Resolve-Path $File).Path
Write-Host ""
Write-Host "  Checking locks on:" -ForegroundColor Cyan
Write-Host "  $File" -ForegroundColor White
Write-Host ""

# ---- Find locking processes via Restart Manager API -------------------------

$rmCode = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class RestartManager {
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint handle, int flags, string key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint handle, uint nFiles, string[] files,
        uint nApps, IntPtr apps, uint nSvcs, string[] svcs);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint handle, out uint needed, ref uint count,
        [In, Out] RM_PROCESS_INFO[] infos, ref uint reasons);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint handle);

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    public static List<int> GetLockingPids(string path) {
        var pids = new List<int>();
        uint session;
        string key = Guid.NewGuid().ToString();
        if (RmStartSession(out session, 0, key) != 0) return pids;
        try {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0)
                return pids;
            uint needed = 0, count = 0, reasons = 0;
            RmGetList(session, out needed, ref count, null, ref reasons);
            if (needed == 0) return pids;
            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return pids;
            foreach (var info in infos) pids.Add(info.Process.dwProcessId);
        } finally {
            RmEndSession(session);
        }
        return pids;
    }
}
"@

Add-Type -TypeDefinition $rmCode -Language CSharp

$lockPids = [RestartManager]::GetLockingPids($File)

if ($lockPids.Count -eq 0) {
    Write-Host "  [ok] No locks found -- file is free." -ForegroundColor Green
    Write-Host ""
    exit 0
}

$procs = @(
    $lockPids | ForEach-Object {
        try { Get-Process -Id $_ -ErrorAction Stop } catch { $null }
    } | Where-Object { $_ -ne $null }
)

if ($procs.Count -eq 0) {
    Write-Host "  [ok] Locking processes already exited." -ForegroundColor Green
    Write-Host ""
    exit 0
}

Write-Host "  Locking processes:" -ForegroundColor Yellow
foreach ($p in $procs) {
    Write-Host ("  [{0,6}]  {1}" -f $p.Id, $p.ProcessName) -ForegroundColor White
}
Write-Host ""

if (-not $Force) {
    $answer = Read-Host "  Kill these processes? [y/N]"
    if ($answer -notmatch '^[Yy]') {
        Write-Host "  Aborted." -ForegroundColor DarkGray
        Write-Host ""
        exit 0
    }
}

$killed = 0
foreach ($p in $procs) {
    try {
        Stop-Process -Id $p.Id -Force -ErrorAction Stop
        Write-Host ("  [killed]  [{0}] {1}" -f $p.Id, $p.ProcessName) -ForegroundColor Green
        $killed++
    } catch {
        Write-Host ("  [failed]  [{0}] {1} -- {2}" -f $p.Id, $p.ProcessName, $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host ("  Done. {0} process(es) killed." -f $killed) -ForegroundColor Cyan
Write-Host ""
