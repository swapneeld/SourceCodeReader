﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.IO;
using SourceCodeReader.Web.Models;
using SourceCodeReader.Web.Infrastructure;
using Roslyn.Services;
using Roslyn.Compilers.Common;
using Ninject.Extensions.Logging;
using System.Threading.Tasks;
using System.Linq.Expressions;

namespace SourceCodeReader.Web.LanguageServices.DotNet
{
    public class DotNetCodeEditorService : IEditorService
    {
        private IApplicationConfigurationProvider applicationConfigurationProvider;
        private ISourceCodeQueryService sourceCodeQueryService;
        private ILogger logger;

        public DotNetCodeEditorService(
            IApplicationConfigurationProvider applicationConfigurationProvider,
            ISourceCodeQueryService sourceCodeQueryService,
            ILogger logger)
        {
            this.applicationConfigurationProvider = applicationConfigurationProvider;
            this.sourceCodeQueryService = sourceCodeQueryService;
            this.logger = logger;
        }

        public string BuildNavigatableSourceCodeFromFile(string username, string project, string path)
        {
            var projectCodeDirectory = this.GetProjectCodeDirectory(username, project);
            string fullPath = Path.Combine(projectCodeDirectory.FullName, path.Replace(@"/", @"\"));
            string fileExtension = Path.GetExtension(fullPath);
            string sourceCode = File.ReadAllText(fullPath);
            
            try
            {
                if (fileExtension == ".cs")
                {
                    var documentInfo = this.sourceCodeQueryService.GetFileDetails(projectCodeDirectory.MakeRelativePath(fullPath));
                    if (documentInfo != null)
                    {
                        var workspace = Roslyn.Services.Workspace.LoadSolution(Path.Combine(projectCodeDirectory.FullName, documentInfo.SolutionPath));
                            //.LoadStandAloneProject(Path.Combine(projectCodeDirectory.FullName, documentInfo.ProjectPath));  // 
                        var solution = workspace.CurrentSolution;
                        IDocument selectedDocument = null;
                        foreach (var projectItem in solution.Projects)
                        {
                            try
                            {                               
                                if (!projectItem.HasDocuments)
                                {
                                    continue;
                                }

                                selectedDocument = projectItem.Documents.SingleOrDefault(document => document.FilePath == fullPath);
                                if (selectedDocument != null)
                                {
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                this.logger.Error(ex, "An error has occured while loading the project {0}", projectItem.Name);
                            }
                        }

                        if (selectedDocument != null)
                        {
                            ISyntaxNavigationBuilder syntaxNavigationBuilder = new DotNetSyntaxNavigationBuilder();
                            var semanticModel = selectedDocument.GetSemanticModel();
                            return syntaxNavigationBuilder.GetCodeAsNavigatableHtml(semanticModel, new CSharpCodeNavigationSyntaxWalker());
                        }
                    }

                    return sourceCode;
                }
            }
            catch (Exception ex)
            {
                this.logger.Error(ex, "Error occured while highlighting sytnax on file {0}", path);
            }
          
            return HttpUtility.HtmlEncode(sourceCode);
        }


        public TokenResult GoToDefinition(TokenParameter parameter, IFindReferenceProgress findReferenceProgressListener)
        {
            try
            {
                return this.sourceCodeQueryService.FindExact(parameter);
            }
            catch (Exception ex)            {

                this.logger.Error(ex, "Error occured while finding definition for {0}", parameter.FullyQualifiedName);
            }

            return null;
        }

        public List<TokenResult> FindRefernces(TokenParameter parameter, IFindReferenceProgress findReferenceProgressListener)
        {
            var result = new List<TokenResult>();
            var findReferenceSyntaxWalker = new FindReferenceSyntaxWalker();
            TraverseThroughAllTheProjectFiles(parameter, findReferenceProgressListener, (documentName, documentRelativePath, semanticModel, syntaxRoot) =>
            {
                findReferenceSyntaxWalker.DoVisit(syntaxRoot, parameter.Text, (foundlocation) =>
                {
                    result.Add(new TokenResult { FileName = documentName, Path = documentRelativePath, Position = foundlocation });
                });

                return false;
            });                               

            findReferenceProgressListener.OnFindReferenceCompleted(result.Count);
            return result;
        }

        private void TraverseThroughAllTheProjectFiles(TokenParameter parameter, IFindReferenceProgress findReferenceProgressListener, Func< string, string, ISemanticModel, CommonSyntaxNode, bool> visitorAction)
        {
            findReferenceProgressListener.OnFindReferenceStarted();

            var projectCodeDirectory = this.GetProjectCodeDirectory(parameter.Username, parameter.Project);
           
            var solutionPath = FindSolutionPath(parameter.Username, parameter.Project);
            if (solutionPath == null)
            {
                findReferenceProgressListener.OnFindReferenceCompleted(0);
                return;
            }

            findReferenceProgressListener.OnFindReferenceInProgress();

            var workspace = Roslyn.Services.Workspace.LoadSolution(solutionPath);
            var currentFilePath = Path.Combine(projectCodeDirectory.FullName, parameter.Path.Replace(@"/", @"\"));
            var solution = workspace.CurrentSolution;

            foreach (var project in solution.Projects)
            {

                try
                {
                    bool processingCompleted = false;

                    if (!project.HasDocuments)
                    {
                        continue;
                    }

                    foreach (var document in project.Documents)
                    {
                        var documentSemanticModel = document.GetSemanticModel();
                        var findReferenceSyntaxtWalker = new FindReferenceSyntaxWalker();
                        CommonSyntaxNode syntaxRoot = null;
                        if (documentSemanticModel.SyntaxTree.TryGetRoot(out syntaxRoot))
                        {
                            var documentRelativePath = new Uri(projectCodeDirectory.FullName + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(document.FilePath)).ToString();
                            processingCompleted = visitorAction(document.Name, documentRelativePath,documentSemanticModel, syntaxRoot);
                            if (processingCompleted)
                            {
                                break;
                            }
                        }
                    }

                    if (processingCompleted)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Error(ex, "An error has occured while loading the project {0}", project.Name);
                }
            }
        }


        private string FindSolutionPath(string username, string project)
        {
            var projectCodeDirectory = this.GetProjectCodeDirectory(username, project);
            var solutions = projectCodeDirectory.GetFiles("*.sln", SearchOption.AllDirectories);

            if (solutions.Length > 0)
            {
                // Check for a solution with project name
                var selectedSolution = solutions.SingleOrDefault(x => Path.GetFileNameWithoutExtension(x.FullName).Equals(project, StringComparison.OrdinalIgnoreCase));
                if (selectedSolution != null)
                {
                    return selectedSolution.FullName;
                }

                return solutions[0].FullName;
            }

            return null;
        }

        private DirectoryInfo GetProjectCodeDirectory(string username, string project)
        {
            var projectSourceCodeDirectory = this.applicationConfigurationProvider.GetProjectSourceCodePath(username, project);
            return new DirectoryInfo(projectSourceCodeDirectory).GetDirectories()[0];
        }


        public void RewriteExternalDependencies(string username, string project)
        {
            var projectDirectory = this.GetProjectCodeDirectory(username, project);
            var projectFiles = projectDirectory.GetFiles("*.csproj", SearchOption.AllDirectories);
            var msBuildExteionpath32 = Path.Combine(this.applicationConfigurationProvider.ApplicationRoot, "TargetFiles", "MSBuild-MS-VS");
            foreach (var projectFile in projectFiles)
            {
                var projectFileContent = File.ReadAllText(projectFile.FullName);
                projectFileContent = projectFileContent.Replace(@"$(MSBuildExtensionsPath32)\Microsoft\VisualStudio", msBuildExteionpath32);
                File.WriteAllText(projectFile.FullName, projectFileContent);
            }

        }
    }
}