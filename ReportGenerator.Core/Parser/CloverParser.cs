using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by Clover.
    /// </summary>
    internal class CloverParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(CloverParser));

        /// <summary>
        /// Initializes a new instance of the <see cref="CloverParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal CloverParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report.</param>
        /// <returns>The parser result.</returns>
        public override ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var modules = report.Descendants("package")
              .ToArray();

            if (modules.Length == 0)
            {
                modules = report.Descendants("project").ToArray();
            }

            var assemblyNames = modules
                .Select(m => m.Attribute("name").Value)
                .Distinct()
                .Where(a => this.AssemblyFilter.IsElementIncludedInReport(a))
                .OrderBy(a => a)
                .ToArray();

            foreach (var assemblyName in assemblyNames)
            {
                assemblies.Add(this.ProcessAssembly(modules, assemblyName));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), true, this.ToString());

            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(XElement[] modules, string assemblyName)
        {
            Logger.DebugFormat("  " + Resources.CurrentAssembly, assemblyName);

            var files = modules
                .Where(m => m.Attribute("name").Value.Equals(assemblyName))
                .Elements("file")
                .Where(f => this.FileFilter.IsElementIncludedInReport(f.Attribute("name").Value))
                .OrderBy(f => f.Attribute("name").Value)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(files, file => this.ProcessFile(assembly, file));

            return assembly;
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <param name="fileElement">The file element.</param>
        private void ProcessFile(Assembly assembly, XElement fileElement)
        {
            var @class = new Class(fileElement.Attribute("name").Value, assembly);

            var lines = fileElement.Elements("line")
                .ToArray();

            var linesOfFile = lines
                .Where(line => line.Attribute("type").Value == "stmt")
                .Select(line => new
                {
                    LineNumber = int.Parse(line.Attribute("num").Value, CultureInfo.InvariantCulture),
                    Visits = int.Parse(line.Attribute("count").Value, CultureInfo.InvariantCulture)
                })
                .OrderBy(seqpnt => seqpnt.LineNumber)
                .ToArray();

            var branches = GetBranches(lines);

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (linesOfFile.Length > 0)
            {
                coverage = new int[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];
                lineVisitStatus = new LineVisitStatus[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var line in linesOfFile)
                {
                    coverage[line.LineNumber] = line.Visits;

                    bool partiallyCovered = false;

                    ICollection<Branch> branchesOfLine = null;
                    if (branches.TryGetValue(line.LineNumber, out branchesOfLine))
                    {
                        partiallyCovered = branchesOfLine.Any(b => b.BranchVisits == 0);
                    }

                    LineVisitStatus statusOfLine = line.Visits > 0 ? (partiallyCovered ? LineVisitStatus.PartiallyCovered : LineVisitStatus.Covered) : LineVisitStatus.NotCovered;
                    lineVisitStatus[line.LineNumber] = statusOfLine;
                }
            }

            var methodsOfFile = lines
                .Where(line => line.Attribute("type").Value == "method")
                .ToArray();

            var codeFile = new CodeFile(fileElement.Attribute("path").Value, coverage, lineVisitStatus, branches);

            SetCodeElements(codeFile, methodsOfFile);

            @class.AddFile(codeFile);

            assembly.AddClass(@class);
        }

        /// <summary>
        /// Gets the branches by line number.
        /// </summary>
        /// <param name="lines">The lines.</param>
        /// <returns>The branches by line number.</returns>
        private static Dictionary<int, ICollection<Branch>> GetBranches(IEnumerable<XElement> lines)
        {
            var result = new Dictionary<int, ICollection<Branch>>();

            foreach (var line in lines)
            {
                if (line.Attribute("type").Value != "cond")
                {
                    continue;
                }

                int lineNumber = int.Parse(line.Attribute("num").Value, CultureInfo.InvariantCulture);

                int negativeBrancheCovered = int.Parse(line.Attribute("falsecount").Value, CultureInfo.InvariantCulture);
                int positiveBrancheCovered = int.Parse(line.Attribute("truecount").Value, CultureInfo.InvariantCulture);

                var branches = new HashSet<Branch>();

                string identifier1 = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}_{1}",
                        lineNumber,
                        "0");

                string identifier2 = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}_{1}",
                        lineNumber,
                        "1");

                branches.Add(new Branch(negativeBrancheCovered > 0 ? 1 : 0, identifier1));
                branches.Add(new Branch(positiveBrancheCovered > 0 ? 1 : 0, identifier2));

                result.Add(lineNumber, branches);
            }

            return result;
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                string methodName = method.Attribute("signature").Value;
                int lineNumber = int.Parse(method.Attribute("num").Value, CultureInfo.InvariantCulture);

                codeFile.AddCodeElement(new CodeElement(methodName, CodeElementType.Method, lineNumber, lineNumber));
            }
        }
    }
}
