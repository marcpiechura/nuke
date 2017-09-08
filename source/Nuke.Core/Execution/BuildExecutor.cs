﻿// Copyright Matthias Koch 2017.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Nuke.Core.OutputSinks;
using Nuke.Core.Utilities;
using static Nuke.Core.EnvironmentInfo;

namespace Nuke.Core.Execution
{
    internal static class BuildExecutor
    {
        public static int Execute<T> (Expression<Func<T, Target>> defaultTargetExpression)
            where T : NukeBuild
        {
            try
            {
                var executionList = Setup(defaultTargetExpression);
                return new ExecutionListRunner().Run(executionList);
            }
            catch (Exception ex)
            {
                OutputSink.Fail(ex.Message, ex.StackTrace);
                return -ex.Message.GetHashCode();
            }
        }

        private static IReadOnlyCollection<TargetDefinition> Setup<T> (Expression<Func<T, Target>> defaultTargetExpression)
            where T : NukeBuild
        {
            var build = Activator.CreateInstance<T>();
            var defaultTargetFactory = defaultTargetExpression.Compile().Invoke(build);

            PrintLogo();

            InjectionService.InjectValues(build);
            //{
            //    Console.WriteLine($"Target: {build.Target.Join(", ")}");
            //    Console.WriteLine($"Verbosity: {build.Verbosity}");
            //    Console.WriteLine($"LogLevel: {build.LogLevel}");
            //    Console.WriteLine($"NoDependencies: {build.NoDependencies}");
            //    Console.WriteLine($"Configuration: {build.Configuration}");
            //}

            if (build.Help != null)
            {
                if (build.Help.Length == 0 || build.Help.Any(x => "targets".StartsWithOrdinalIgnoreCase(x)))
                    Logger.Log(GetTargetsText(build, defaultTargetFactory));
                if (build.Help.Length == 0 || build.Help.Any(x => "parameters".StartsWithOrdinalIgnoreCase(x)))
                    Logger.Log(GetParametersText(build));
                if (build.Help.Any(x => "graph".StartsWithOrdinalIgnoreCase(x)))
                    ShowGraph(build, defaultTargetFactory);

                Environment.Exit(exitCode: 0);
            }

            var executionList = TargetDefinitionLoader.GetExecutionList(build, defaultTargetFactory);
            RequirementService.ValidateRequirements(executionList, build);
            return executionList;
        }

        private static void PrintLogo()
        {
            var assembly = typeof(BuildExecutor).GetTypeInfo().Assembly;
            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute> ().InformationalVersion;
            var details = fileVersion != "1.0.0.0"
                ? $"Version: {fileVersion} [CommitSha: {informationalVersion.Substring(informationalVersion.LastIndexOf(value: '.') + 1, length: 8)}]"
                : "LOCAL VERSION";

            Logger.Log(FigletTransform.GetText("NUKE"));
            Logger.Log(details);
            Logger.Log(string.Empty);
        }

        public static string GetTargetsText<T> (T build, Target defaultTargetFactory)
            where T : NukeBuild
        {
            var builder = new StringBuilder();

            var targetDefinitions = build.GetTargetDefinitions(defaultTargetFactory);
            var longestTargetName = targetDefinitions.Select(x => x.Name.Length).OrderByDescending(x => x).First();
            var padRightTargets = Math.Max(longestTargetName, val2: 20);
            builder.AppendLine($"  Targets:");
            builder.AppendLine();
            foreach (var target in targetDefinitions)
            {
                var dependencies = target.TargetDefinitionDependencies.Count > 0
                    ? $" -> {target.TargetDefinitionDependencies.Select(x => x.Name).Join(", ")}"
                    : string.Empty;
                builder.AppendLine($"  {(target.IsDefault ? ">" : " ")} {target.Name.PadRight(padRightTargets)}{dependencies}");
                if (!string.IsNullOrWhiteSpace(target.Description))
                    builder.AppendLine($"      {target.Description}");
            }

            return builder.ToString();
        }

        public static string GetParametersText<T> (T build)
            where T : NukeBuild
        {
            var builder = new StringBuilder();

            var parameters = build.GetParameterMembers().OrderBy(x => x.Name).ToList();
            var padRightParameter = Math.Max(parameters.Max(x => x.Name.Length), val2: 17);

            void PrintParameter (MemberInfo parameter)
            {
                var attribute = parameter.GetCustomAttribute<ParameterAttribute>();
                var description = SplitLines(attribute.Description ?? "<no description>");
                builder.AppendLine($"    -{(attribute.Name ?? parameter.Name).PadRight(padRightParameter)}  {description.First()}");
                foreach (var line in description.Skip(count: 1))
                    builder.AppendLine($"{new string(c: ' ', count: padRightParameter + 7)}{line}");
            }

            builder.AppendLine("  Parameters:");

            var customParameters = parameters.Where(x => x.DeclaringType != typeof(NukeBuild)).ToList();
            if (customParameters.Count > 0)
                builder.AppendLine();
            foreach (var parameter in customParameters)
                PrintParameter(parameter);
            
            builder.AppendLine();

            var inheritedParameters = parameters.Where(x => x.DeclaringType == typeof(NukeBuild));
            foreach (var parameter in inheritedParameters)
                PrintParameter(parameter);

            return builder.ToString();
        }

        private static List<string> SplitLines(string text)
        {
            var words = new Queue<string>(text.Split(' ').ToList());
            var lines = new List<string> { string.Empty };
            foreach (var word in words)
            {
                if (lines.Last().Length + word.Length > 100)
                    lines.Add(string.Empty);

                lines[lines.Count - 1] = $"{lines.Last()} {word}";
            }
            return lines;
        }

        private static void ShowGraph<T> (T build, Target defaultTargetFactory)
            where T : NukeBuild
        {
            string GetStringFromStream (Stream stream)
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }

            var assembly = typeof(BuildExecutor).GetTypeInfo().Assembly;
            var resourceName = typeof(BuildExecutor).Namespace + ".graph.html";
            var resourceStream = assembly.GetManifestResourceStream(resourceName).NotNull("resourceStream != null");

            var targetDefinitions = build.GetTargetDefinitions(defaultTargetFactory);
            var dependencies =
                    from target in targetDefinitions
                    from dependency in target.TargetDefinitionDependencies
                    select new { Source = dependency.Name, Destination = target.Name};
            var links = dependencies.Aggregate(
                new StringBuilder(),
                (sb, link) => sb.AppendLine($"{link.Source} --> {link.Destination}"),
                sb => sb.ToString());

            var path = Path.Combine(build.TemporaryDirectory, "graph.html");
            var contents = GetStringFromStream(resourceStream).Replace("__DEPENDENCIES__", links);
            File.WriteAllText(path, contents);
            Process.Start(path);
        }
    }
}
