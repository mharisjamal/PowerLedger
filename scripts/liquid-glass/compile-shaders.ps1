# Compiles the liquid glass shaders (src/PowerLedger.App/Aero/LiquidGlass/Shaders/*.fx) to the .ps files the app embeds,
# with Windows' own d3dcompiler_47.dll (no SDK or fxc needed). Run it after editing a .fx and commit the .ps it writes.
param([string]$Profile = 'ps_2_0')
$source = @'
using System;
using System.Runtime.InteropServices;
public static class Fxc
{
    [DllImport("d3dcompiler_47.dll")]
    private static extern int D3DCompile(byte[] data, IntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetPointer(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetSize(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Release(IntPtr self);
    private static byte[] Bytes(IntPtr blob)
    {
        var vtable = Marshal.ReadIntPtr(blob);
        var pointer = ((GetPointer)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(GetPointer)))(blob);
        var size = (int)((GetSize)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size), typeof(GetSize)))(blob);
        var bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, size);
        ((Release)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size), typeof(Release)))(blob);
        return bytes;
    }
    public static byte[] Compile(string text, string name, string target)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        // D3DCOMPILE_OPTIMIZATION_LEVEL3 (1 << 15)
        IntPtr code, errors;
        var hr = D3DCompile(data, (IntPtr)data.Length, name, IntPtr.Zero, IntPtr.Zero, "main", target, 1u << 15, 0, out code, out errors);
        if (hr < 0) throw new Exception(errors != IntPtr.Zero ? System.Text.Encoding.UTF8.GetString(Bytes(errors)) : "D3DCompile failed: 0x" + hr.ToString("X8"));
        if (errors != IntPtr.Zero) Bytes(errors);
        return Bytes(code);
    }
}
'@
Add-Type -TypeDefinition $source
$folder = Resolve-Path "$PSScriptRoot\..\..\src\PowerLedger.App\Aero\LiquidGlass\Shaders"
foreach ($fx in Get-ChildItem $folder -Filter *.fx) {
    $bytes = [Fxc]::Compile((Get-Content $fx.FullName -Raw), $fx.Name, $Profile)
    [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($fx.FullName, '.ps'), $bytes)
    "{0} -> {1} bytes ({2})" -f $fx.Name, $bytes.Length, $Profile
}
