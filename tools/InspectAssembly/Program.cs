using System.Reflection;
using System.Runtime.InteropServices;

static void Dump(string path)
{
    path = Path.GetFullPath(path);
    Console.WriteLine($"Assembly: {path}");

    var asm = Assembly.LoadFrom(path);

    Console.WriteLine($"FullName: {asm.FullName}");
    Console.WriteLine("Referenced assemblies:");
    foreach (var r in asm.GetReferencedAssemblies().OrderBy(a => a.Name))
        Console.WriteLine($"  - {r.FullName}");

    Console.WriteLine();
    Console.WriteLine("Types:");
    foreach (var t in asm.GetTypes().OrderBy(t => t.FullName))
        Console.WriteLine($"  - {t.FullName}");

    Console.WriteLine();
    Console.WriteLine("P/Invoke methods:");
    foreach (var t in asm.GetTypes())
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
    {
        var attrs = m.GetCustomAttributes<DllImportAttribute>().ToArray();
        if (attrs.Length == 0) continue;
        foreach (var a in attrs)
            Console.WriteLine($"  - {t.FullName}.{m.Name} -> {a.Value} ({a.EntryPoint})");
    }
}

static void DumpTypeMembers(string assemblyPath, string typeName)
{
    assemblyPath = Path.GetFullPath(assemblyPath);
    var asm = Assembly.LoadFrom(assemblyPath);
    var t = asm.GetType(typeName, throwOnError: true, ignoreCase: false)!;

    Console.WriteLine($"Type: {t.FullName}");
    Console.WriteLine($"BaseType: {t.BaseType?.FullName}");
    Console.WriteLine($"IsEnum: {t.IsEnum}  IsValueType: {t.IsValueType}  IsClass: {t.IsClass}");

    if (t.IsEnum)
    {
        Console.WriteLine("Enum values:");
        foreach (var n in Enum.GetNames(t))
            Console.WriteLine($"  - {n} = {Convert.ToInt64(Enum.Parse(t, n))}");
    }

    Console.WriteLine();
    Console.WriteLine("Public properties:");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(p => p.Name))
        Console.WriteLine($"  - {p.PropertyType.Name} {p.Name} (static={p.GetGetMethod()?.IsStatic == true})");

    Console.WriteLine();
    Console.WriteLine("Public methods:");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                 .Where(m => !m.IsSpecialName)
                 .OrderBy(m => m.Name))
    {
        var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  - {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({pars})");
    }
}

var target = args.Length > 0
    ? args[0]
    : Path.Combine("..", "..", "..", "..", "Dongled", "bin", "Debug", "net8.0-windows", "Plugins", "Dongled.Plugin.CorsairIcue.dll");

if (args.Length >= 2)
{
    DumpTypeMembers(target, args[1]);
}
else
{
    Dump(target);
}
