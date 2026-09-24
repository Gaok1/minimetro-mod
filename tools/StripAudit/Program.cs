using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Audita referencias de uma assembly contra o conjunto de assemblies que o jogo
// realmente carrega. Existe porque a IMGUIModule nao-stripada chama membros que
// o stripping do jogo removeu do CoreModule/mscorlib -- e isso so aparece como
// MissingMethodException em runtime, dentro do OnGUI.
//
// uso: StripAudit <assembly-a-escanear> <dir-de-referencia> [mais dirs...]

if (args.Length < 2)
{
    Console.Error.WriteLine("uso: StripAudit <scan.dll> <refdir> [refdir...]");
    return 1;
}

var scanPath = args[0];
var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

for (int i = 1; i < args.Length; i++)
{
    foreach (var dll in Directory.GetFiles(args[i], "*.dll"))
    {
        try { IndexAssembly(dll, index); }
        catch (Exception e) { Console.Error.WriteLine($"[skip] {Path.GetFileName(dll)}: {e.Message}"); }
    }
}

Console.WriteLine($"indexados {index.Count} tipos a partir de {args.Length - 1} pasta(s)");
Console.WriteLine($"escaneando {Path.GetFileName(scanPath)}\n");

var missingTypes = new SortedSet<string>(StringComparer.Ordinal);
int checkedRefs = 0, genericRefs = 0;
var missingMembers = new SortedSet<string>(StringComparer.Ordinal);

using (var fs = File.OpenRead(scanPath))
using (var pe = new PEReader(fs))
{
    var md = pe.GetMetadataReader();

    foreach (var handle in md.MemberReferences)
    {
        var mr = md.GetMemberReference(handle);
        // Metodo de tipo generico instanciado (List<int>.GetRange) tem como pai
        // uma TypeSpec; o tipo de verdade e a definicao generica dentro dela.
        // Antes isto era pulado, e o GetRange stripado passou pela auditoria.
        TypeReferenceHandle trh;
        if (mr.Parent.Kind == HandleKind.TypeReference) trh = (TypeReferenceHandle)mr.Parent;
        else if (mr.Parent.Kind == HandleKind.TypeSpecification && GenericDefinition(md, (TypeSpecificationHandle)mr.Parent, out trh)) genericRefs++;
        else continue;

        var tr = md.GetTypeReference(trh);
        var full = FullName(md, tr);
        if (full == null) continue;                       // tipo aninhado: ignorado
        if (!IsInterestingScope(md, tr)) continue;

        var name = md.GetString(mr.Name);
        checkedRefs++;

        if (!index.TryGetValue(full, out var members))
        {
            missingTypes.Add(full);
            continue;
        }
        if (!members.Contains(name))
            missingMembers.Add($"{full}::{name}");
    }
}

if (missingTypes.Count > 0)
{
    Console.WriteLine($"== TIPOS AUSENTES ({missingTypes.Count}) ==");
    foreach (var t in missingTypes) Console.WriteLine("  " + t);
    Console.WriteLine();
}

Console.WriteLine($"{checkedRefs} referencias conferidas ({genericRefs} em tipo generico instanciado)\n");
Console.WriteLine($"== MEMBROS AUSENTES ({missingMembers.Count}) ==");
foreach (var m in missingMembers) Console.WriteLine("  " + m);

return 0;

// ----------------------------------------------------------------------------
static void IndexAssembly(string path, Dictionary<string, HashSet<string>> index)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    if (!pe.HasMetadata) return;
    var md = pe.GetMetadataReader();

    foreach (var handle in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(handle);
        var ns = md.GetString(td.Namespace);
        var nm = md.GetString(td.Name);
        var full = string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;

        if (!index.TryGetValue(full, out var members))
            index[full] = members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mh in td.GetMethods()) members.Add(md.GetString(md.GetMethodDefinition(mh).Name));
        foreach (var fh in td.GetFields()) members.Add(md.GetString(md.GetFieldDefinition(fh).Name));
    }
}

// GENERICINST (CLASS|VALUETYPE) TypeDefOrRef ...: devolve o TypeRef da definicao.
static bool GenericDefinition(MetadataReader md, TypeSpecificationHandle h, out TypeReferenceHandle tr)
{
    tr = default;
    var br = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
    if (br.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return false;
    br.ReadSignatureTypeCode(); // CLASS ou VALUETYPE
    var th = br.ReadTypeHandle();
    if (th.Kind != HandleKind.TypeReference) return false;
    tr = (TypeReferenceHandle)th;
    return true;
}

static string FullName(MetadataReader md, TypeReference tr)
{
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return null;
    var ns = md.GetString(tr.Namespace);
    var nm = md.GetString(tr.Name);
    return string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
}

// So interessa o que vem de assemblies que o jogo carrega. Referencias a
// System.* que nem existem no jogo sao ruido de outro flavor da BCL.
static bool IsInterestingScope(MetadataReader md, TypeReference tr)
{
    if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
    var ar = md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
    var n = md.GetString(ar.Name);
    return n is "mscorlib" or "netstandard" || n.StartsWith("UnityEngine", StringComparison.Ordinal);
}
