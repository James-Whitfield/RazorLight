using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Options;
using RazorLight.Generation;
using RazorLight.Internal;
using DependencyContextCompilationOptions = Microsoft.Extensions.DependencyModel.CompilationOptions;

namespace RazorLight.Compilation
{
	public class RoslynCompilationService : ICompilationService
	{
		private static readonly bool DiagnosticsEnabled = string.Equals(
			Environment.GetEnvironmentVariable("RAZORLIGHT_DIAGNOSTICS"),
			"1",
			StringComparison.Ordinal);

		private static readonly bool LoadWithoutSymbols = string.Equals(
			Environment.GetEnvironmentVariable("RAZORLIGHT_LOAD_WITHOUT_SYMBOLS"),
			"1",
			StringComparison.Ordinal);

		private static readonly bool ForceLoadWithSymbols = string.Equals(
			Environment.GetEnvironmentVariable("RAZORLIGHT_LOAD_WITH_SYMBOLS"),
			"1",
			StringComparison.Ordinal);

		private readonly IMetadataReferenceManager metadataReferenceManager;
		private readonly bool isDevelopment;
		private readonly List<MetadataReference> metadataReferences = new List<MetadataReference>();
		private readonly IPrecompileCallback precompileCallback;
		private readonly bool loadDynamicAssemblyWithSymbols;

		public RoslynCompilationService(IMetadataReferenceManager referenceManager, Assembly operatingAssembly, IPrecompileCallback precompileCallback = null, bool? loadDynamicAssemblyWithSymbols = null)
		{
			this.metadataReferenceManager = referenceManager ?? throw new ArgumentNullException(nameof(referenceManager));
			this.OperatingAssembly = operatingAssembly ?? throw new ArgumentNullException(nameof(operatingAssembly));
			this.precompileCallback = precompileCallback;
			this.loadDynamicAssemblyWithSymbols = ResolveLoadDynamicAssemblyWithSymbols(loadDynamicAssemblyWithSymbols);

			isDevelopment = AssemblyDebugModeUtility.IsAssemblyDebugBuild(OperatingAssembly);
			var pdbFormat = SymbolsUtility.SupportsFullPdbGeneration() ?
				DebugInformationFormat.Pdb :
				DebugInformationFormat.PortablePdb;

			EmitOptions = new EmitOptions(debugInformationFormat: pdbFormat);
		}

		public RoslynCompilationService(IMetadataReferenceManager referenceManager, IOptions<RazorLightOptions> options, IPrecompileCallback precompileCallback = null) :
			this(referenceManager, options.Value.OperatingAssembly, precompileCallback, options.Value.LoadDynamicAssemblyWithSymbols)
		{

		}

		#region Options

		public virtual Assembly OperatingAssembly { get; }

		public virtual EmitOptions EmitOptions { get; }
		public virtual CSharpCompilationOptions CSharpCompilationOptions
		{
			get
			{
				EnsureOptions();
				return _compilationOptions;
			}
		}
		public virtual CSharpParseOptions ParseOptions
		{
			get
			{
				EnsureOptions();
				return _parseOptions;
			}
		}

		#endregion

		private CSharpParseOptions _parseOptions;
		private CSharpCompilationOptions _compilationOptions;

		private static readonly object locker = new object();

		private bool _optionsInitialized;
		private void EnsureOptions()
		{
			lock (locker)
			{
				if (!_optionsInitialized)
				{
					var dependencyContextOptions = GetDependencyContextCompilationOptions();
					_parseOptions = GetParseOptions(dependencyContextOptions);
					_compilationOptions = GetCompilationOptions(dependencyContextOptions);

					metadataReferences.AddRange(metadataReferenceManager.Resolve(OperatingAssembly));

					_optionsInitialized = true;
				}
			}
		}


		public Assembly CompileAndEmit(IGeneratedRazorTemplate razorTemplate)
		{
			if (razorTemplate == null)
			{
				throw new ArgumentNullException(nameof(razorTemplate));
			}

			string assemblyName = Path.GetRandomFileName();
			LogDiagnostic($"CompileAndEmit start assemblyName='{assemblyName}' templateKind='{razorTemplate.GetType().FullName}'");
			var compilation = CreateCompilation(razorTemplate.GeneratedCode, assemblyName);

			using (var assemblyStream = new MemoryStream())
			using (var pdbStream = new MemoryStream())
			{
				var result = compilation.Emit(
					assemblyStream,
					pdbStream,
					options: EmitOptions);

				if (!result.Success)
				{
					List<Diagnostic> errorsDiagnostics = result.Diagnostics
							.Where(d => d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error)
							.ToList();

					StringBuilder builder = new StringBuilder();
					builder.AppendLine("Failed to compile generated Razor template:");

					var compilationDiagnostics = new List<TemplateCompilationDiagnostic>();

					foreach (Diagnostic diagnostic in errorsDiagnostics)
					{
						FileLinePositionSpan lineSpan = diagnostic.Location.SourceTree.GetMappedLineSpan(diagnostic.Location.SourceSpan);
						string errorMessage = diagnostic.GetMessage();
						string formattedMessage = $"- ({lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}) {errorMessage}";

						var compilationDiagnostic = new TemplateCompilationDiagnostic(errorMessage, formattedMessage, lineSpan);
						compilationDiagnostics.Add(compilationDiagnostic);

						builder.AppendLine(formattedMessage);
					}

					builder.AppendLine("\nSee CompilationErrors for detailed information");

					throw new TemplateCompilationException(builder.ToString(), compilationDiagnostics);
				}

				assemblyStream.Seek(0, SeekOrigin.Begin);
				pdbStream.Seek(0, SeekOrigin.Begin);

				var rawAssembly = assemblyStream.ToArray();
				var rawSymbolStore = pdbStream.ToArray();
				LogDiagnostic($"CompileAndEmit emit success assemblyName='{assemblyName}' assemblyBytes='{rawAssembly.Length}' pdbBytes='{rawSymbolStore.Length}'");
				precompileCallback?.Invoke(razorTemplate, rawAssembly, rawSymbolStore);
				LogDiagnostic($"CompileAndEmit about to load assembly assemblyName='{assemblyName}'");
				Assembly assembly;
				try
				{
					if (!loadDynamicAssemblyWithSymbols)
					{
						LogDiagnostic($"CompileAndEmit loading without symbols assemblyName='{assemblyName}'");
						assembly = Assembly.Load(rawAssembly);
					}
					else
					{
						assembly = Assembly.Load(rawAssembly, rawSymbolStore);
					}
				}
				catch (Exception ex)
				{
					LogDiagnostic($"CompileAndEmit load exception assemblyName='{assemblyName}' type='{ex.GetType().FullName}' message='{ex.Message}'");
					throw;
				}

				LogDiagnostic($"CompileAndEmit load success assemblyName='{assembly.FullName}'");

				return assembly;
			}
		}

		private static void LogDiagnostic(string message)
		{
			if (!DiagnosticsEnabled)
			{
				return;
			}

			Console.Error.WriteLine($"[RazorLightDiag {DateTime.UtcNow:O}] {message}");
		}

		private static bool ResolveLoadDynamicAssemblyWithSymbols(bool? optionValue)
		{
			if (LoadWithoutSymbols)
			{
				return false;
			}

			if (ForceLoadWithSymbols)
			{
				return true;
			}

			if (optionValue.HasValue)
			{
				return optionValue.Value;
			}

			// Linux runtimes have shown hard crashes in Assembly.Load(byte[], byte[]) for generated templates.
			// Prefer symbol-free load by default on Linux for stability.
			return !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
		}

		protected internal virtual DependencyContextCompilationOptions GetDependencyContextCompilationOptions()
		{
			var dependencyContext = DependencyContext.Load(OperatingAssembly);

			if (dependencyContext?.CompilationOptions != null)
			{
				return dependencyContext.CompilationOptions;
			}

			return DependencyContextCompilationOptions.Default;
		}

		private CSharpCompilation CreateCompilation(string compilationContent, string assemblyName)
		{
			SourceText sourceText = SourceText.From(compilationContent, Encoding.UTF8);
			SyntaxTree syntaxTree = CreateSyntaxTree(sourceText).WithFilePath(assemblyName);

			CSharpCompilation compilation = CreateCompilation(assemblyName).AddSyntaxTrees(syntaxTree);

			compilation = ExpressionRewriter.Rewrite(compilation);

			//var compilationContext = new RoslynCompilationContext(compilation);
			//_compilationCallback(compilationContext);
			//compilation = compilationContext.Compilation;
			return compilation;
		}

		public CSharpCompilation CreateCompilation(string assemblyName)
		{
			return CSharpCompilation.Create(
				assemblyName,
				options: CSharpCompilationOptions,
				references: metadataReferences);
		}

		public SyntaxTree CreateSyntaxTree(SourceText sourceText)
		{
			return CSharpSyntaxTree.ParseText(sourceText, options: ParseOptions);
		}

		private CSharpCompilationOptions GetCompilationOptions(DependencyContextCompilationOptions dependencyContextOptions)
		{
			var csharpCompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

			// Disable 1702 until roslyn turns this off by default
			csharpCompilationOptions = csharpCompilationOptions.WithSpecificDiagnosticOptions(
				new Dictionary<string, ReportDiagnostic>
				{
					{"CS1701", ReportDiagnostic.Suppress}, // Binding redirects
					{"CS1702", ReportDiagnostic.Suppress},
					{"CS1705", ReportDiagnostic.Suppress}
				});

			if (dependencyContextOptions.AllowUnsafe.HasValue)
			{
				csharpCompilationOptions = csharpCompilationOptions.WithAllowUnsafe(
					dependencyContextOptions.AllowUnsafe.Value);
			}

			OptimizationLevel optimizationLevel;
			if (dependencyContextOptions.Optimize.HasValue)
			{
				optimizationLevel = dependencyContextOptions.Optimize.Value ?
					OptimizationLevel.Release :
					OptimizationLevel.Debug;
			}
			else
			{
				optimizationLevel = isDevelopment ?
					OptimizationLevel.Debug :
					OptimizationLevel.Release;
			}
			csharpCompilationOptions = csharpCompilationOptions.WithOptimizationLevel(optimizationLevel);

			if (dependencyContextOptions.WarningsAsErrors.HasValue)
			{
				var reportDiagnostic = dependencyContextOptions.WarningsAsErrors.Value ?
					ReportDiagnostic.Error :
					ReportDiagnostic.Default;
				csharpCompilationOptions = csharpCompilationOptions.WithGeneralDiagnosticOption(reportDiagnostic);
			}

			return csharpCompilationOptions;
		}

		private CSharpParseOptions GetParseOptions(DependencyContextCompilationOptions dependencyContextOptions)
		{
			var configurationSymbol = isDevelopment ? "DEBUG" : "RELEASE";
			var defines = dependencyContextOptions.Defines.Concat(new[] { configurationSymbol });

			var parseOptions = new CSharpParseOptions(preprocessorSymbols: defines);

			if (!string.IsNullOrEmpty(dependencyContextOptions.LanguageVersion))
			{
				if (LanguageVersionFacts.TryParse(dependencyContextOptions.LanguageVersion, out var languageVersion))
				{
					parseOptions = parseOptions.WithLanguageVersion(languageVersion);
				}
				// Newer SDKs may emit language versions unknown to the bundled Roslyn package.
				// Fall back to the parser default instead of failing in Debug builds.
			}

			return parseOptions;
		}
	}
}
