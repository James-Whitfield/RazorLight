using System;
using System.Threading.Tasks;
using System.IO;
using Xunit;

namespace RazorLight.WebSdk.Tests
{
	public sealed class TestModel
	{
		public string Name { get; set; } = string.Empty;
		public int Total { get; set; }
	}

	public class WebSdkCompatibilityTests
	{
		[Fact]
		public async Task CompileRenderStringAsync_Works_In_WebSdk_Project()
		{
			var engine = new RazorLightEngineBuilder()
				.UseMemoryCachingProvider()
				.Build();

			var result = await engine.CompileRenderStringAsync(
				"web-sdk-template",
				"Hello @Model.Name from WebSdk",
				new { Name = "RazorLight" });

			Assert.Equal("Hello RazorLight from WebSdk", result);
		}

		[Fact]
		public async Task CompileRenderAsync_Loads_Cshtml_And_Binds_Model_In_WebSdk_Project()
		{
			var projectRoot = AppContext.BaseDirectory;
			var templateKey = "Templates/WebFileTemplate.cshtml";
			var templatePath = Path.Combine(projectRoot, "Templates", "WebFileTemplate.cshtml");

			Assert.True(File.Exists(templatePath), $"Expected template file at '{templatePath}'");

			var engine = new RazorLightEngineBuilder()
				.UseFileSystemProject(projectRoot)
				.UseMemoryCachingProvider()
				.Build();

			var result = await engine.CompileRenderAsync(templateKey, new TestModel
			{
				Name = "RazorLight",
				Total = 42
			});

			Assert.Equal("Hello RazorLight, total: 42", result.Trim());
		}
	}
}
