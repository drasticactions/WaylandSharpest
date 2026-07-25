using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace WaylandSharpest.Generator
{
    [Generator(LanguageNames.CSharp)]
    public sealed class WaylandProtocolGenerator : IIncrementalGenerator
    {
        private static readonly DiagnosticDescriptor MalformedXml = new DiagnosticDescriptor(
            "WLSG001",
            "Malformed Wayland protocol XML",
            "Protocol file '{0}' could not be processed: {1}",
            "WaylandSharpest",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor UnresolvedReference = new DiagnosticDescriptor(
            "WLSG002",
            "Unresolved Wayland interface reference",
            "{0}",
            "WaylandSharpest",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor DuplicateInterface = new DiagnosticDescriptor(
            "WLSG003",
            "Duplicate Wayland interface",
            "Interface '{0}' is defined by more than one protocol file; the definition in '{1}' is ignored",
            "WaylandSharpest",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal record struct XmlInput(string Path, string? Text, string Namespace);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var xmlFiles = context.AdditionalTextsProvider
                .Where(static file => file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select(static (pair, cancellationToken) =>
                {
                    var (file, optionsProvider) = pair;
                    var options = optionsProvider.GetOptions(file);
                    options.TryGetValue("build_metadata.AdditionalFiles.WaylandNamespace", out var ns);
                    if (string.IsNullOrEmpty(ns))
                    {
                        optionsProvider.GlobalOptions.TryGetValue("build_property.RootNamespace", out ns);
                    }

                    if (string.IsNullOrEmpty(ns))
                    {
                        ns = "WaylandProtocols";
                    }

                    return new XmlInput(file.Path, file.GetText(cancellationToken)?.ToString(), ns!);
                })
                .Where(static input => input.Text is not null)
                .Collect();

            var referencedInterfaces = context.CompilationProvider
                .Select(static (compilation, cancellationToken) => BuildReferenceMap(compilation, cancellationToken));

            context.RegisterSourceOutput(
                xmlFiles.Combine(referencedInterfaces),
                static (productionContext, pair) => Execute(productionContext, pair.Left, pair.Right));
        }

        /// <summary>
        /// Scans referenced assemblies for types carrying [WaylandProxy]/[WaylandResource]
        /// so protocols can reference interfaces compiled elsewhere.
        /// </summary>
        private static ImmutableDictionary<string, (string ProxyFqn, string? ResourceFqn)> BuildReferenceMap(
            Compilation compilation,
            System.Threading.CancellationToken cancellationToken)
        {
            var proxies = new Dictionary<string, string>(StringComparer.Ordinal);
            var resources = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = assembly.Name;
                if (name.StartsWith("System", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft", StringComparison.Ordinal)
                    || name is "mscorlib" or "netstandard" or "WindowsBase")
                {
                    continue;
                }

                Walk(assembly.GlobalNamespace);
            }

            var builder = ImmutableDictionary.CreateBuilder<string, (string, string?)>(StringComparer.Ordinal);
            foreach (var pair in proxies)
            {
                resources.TryGetValue(pair.Key, out var resource);
                builder[pair.Key] = (pair.Value, resource);
            }

            return builder.ToImmutable();

            void Walk(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    Inspect(type);
                }

                foreach (var child in ns.GetNamespaceMembers())
                {
                    Walk(child);
                }
            }

            void Inspect(INamedTypeSymbol type)
            {
                foreach (var attribute in type.GetAttributes())
                {
                    var attributeClass = attribute.AttributeClass;
                    if (attributeClass is null
                        || attributeClass.ContainingNamespace?.ToDisplayString() != "Wayland"
                        || attribute.ConstructorArguments.Length != 1
                        || attribute.ConstructorArguments[0].Value is not string interfaceName)
                    {
                        continue;
                    }

                    var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (attributeClass.Name == "WaylandProxyAttribute" && !proxies.ContainsKey(interfaceName))
                    {
                        proxies[interfaceName] = fqn;
                    }
                    else if (attributeClass.Name == "WaylandResourceAttribute" && !resources.ContainsKey(interfaceName))
                    {
                        resources[interfaceName] = fqn;
                    }
                }
            }
        }

        private static void Execute(
            SourceProductionContext context,
            ImmutableArray<XmlInput> inputs,
            ImmutableDictionary<string, (string ProxyFqn, string? ResourceFqn)> referenced)
        {
            var protocols = new List<(ProtocolModel Protocol, string Path)>();
            foreach (var input in inputs)
            {
                try
                {
                    var protocol = ProtocolParser.Parse(input.Text!, input.Namespace);
                    if (protocol is not null)
                    {
                        protocols.Add((protocol, input.Path));
                    }
                }
                catch (ProtocolParseException ex)
                {
                    context.ReportDiagnostic(Diagnostic.Create(MalformedXml, Location.None, input.Path, ex.Message));
                }
            }

            if (protocols.Count == 0)
            {
                return;
            }

            // Locally defined interfaces take precedence over referenced assemblies.
            var map = new Dictionary<string, InterfaceRef>(StringComparer.Ordinal);
            foreach (var (protocol, path) in protocols)
            {
                foreach (var iface in protocol.Interfaces)
                {
                    if (map.ContainsKey(iface.Name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DuplicateInterface, Location.None, iface.Name, path));
                        continue;
                    }

                    var cls = NameUtils.Pascal(iface.Name);
                    map[iface.Name] = new InterfaceRef(
                        $"global::{protocol.Namespace}.{cls}",
                        $"global::{protocol.Namespace}.{cls}Resource",
                        iface);
                }
            }

            foreach (var pair in referenced)
            {
                if (!map.ContainsKey(pair.Key))
                {
                    map[pair.Key] = new InterfaceRef(pair.Value.ProxyFqn, pair.Value.ResourceFqn, null);
                }
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            var emitter = new ProtocolEmitter(map, message =>
            {
                if (reported.Add(message))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnresolvedReference, Location.None, message));
                }
            });

            var usedHintNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (protocol, path) in protocols)
            {
                // Skip interfaces that lost the duplicate race to avoid emitting
                // colliding class names.
                var deduped = new ProtocolModel(
                    protocol.Name,
                    protocol.Namespace,
                    protocol.Interfaces.Where(i => ReferenceEquals(map[i.Name].Local, i)).ToList());
                if (deduped.Interfaces.Count == 0)
                {
                    continue;
                }

                string hint = protocol.Name;
                var i = 1;
                while (!usedHintNames.Add(hint))
                {
                    hint = protocol.Name + "_" + i++;
                }

                try
                {
                    context.AddSource($"{hint}.g.cs", SourceText.From(emitter.Emit(deduped), System.Text.Encoding.UTF8));
                }
                catch (ProtocolParseException ex)
                {
                    context.ReportDiagnostic(Diagnostic.Create(MalformedXml, Location.None, path, ex.Message));
                }
            }
        }
    }
}
