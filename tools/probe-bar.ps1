# Dev check: prints every monitor's bounds and work area, and the Stillbar window rectangle (physical pixels).
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class StillbarProbe {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
}
'@
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) { "screen $($s.DeviceName) primary=$($s.Primary) bounds=$($s.Bounds) work=$($s.WorkingArea)" }
$h = [StillbarProbe]::FindWindowW([NullString]::Value, 'Stillbar')
if ($h -ne [IntPtr]::Zero) { $r = New-Object StillbarProbe+R; [void][StillbarProbe]::GetWindowRect($h, [ref]$r); "window L=$($r.L) T=$($r.T) R=$($r.Rt) B=$($r.B)" } else { 'window not found' }

