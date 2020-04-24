﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework.Constraints;
using RoslynObfuscator.Obfuscation.Cryptography;
using RoslynObfuscator.Obfuscation.InjectedClasses;


namespace RoslynObfuscator.Obfuscation
{
    public enum ObfuscationType
    {
        IdentifierObfuscation,
        StringEncryption,
        PInvokeObfuscation,
        EmbeddedResourceObfuscation,
        NamespaceObfuscation,
        TypeReflectionObfuscation
    }
    public class SourceObfuscator
    {
        private Dictionary<NameSyntax, string> renamedNamespaces;
        private Dictionary<string, string> renamedMembers;

        //HACK: Currently we just replace everything with the same Namespace since we're going 
        //      to rename EVERYTHING which should avoid collisions and we don't know if a file
        //      has implicitly imported a different file by using the same namespace
        private string ObfuscatedNamespace;

        private PolymorphicCodeOptions polymorphicCodeOptions;

        private List<EmbeddedResourceData> assemblyResources;

        private static readonly string[] LibrarariesToIgnore = 
        {
            "mscorlib",
        };


		public SourceObfuscator(PolymorphicCodeOptions codeOptions = null)
        {
            polymorphicCodeOptions = codeOptions;

            if (codeOptions == null)
            {
                polymorphicCodeOptions = PolymorphicCodeOptions.Default;
            }

            //Generate the encryption key we'll use for this set of obfuscation
            string encryptionKey = PolymorphicGenerator.GetRandomString(polymorphicCodeOptions);
            StringEncryptor.Key = encryptionKey;

            ObfuscatedNamespace = PolymorphicGenerator.GetRandomIdentifier(polymorphicCodeOptions);

            assemblyResources = new List<EmbeddedResourceData>();

            renamedMembers = new Dictionary<string, string>();
            renamedNamespaces = new Dictionary<NameSyntax, string>();
        }

        private string GetSymbolTokenLookupKey(SyntaxToken token, ISymbol symbol)
        {
            if (symbol == null)
            {
                string guessedLookupName = this.renamedMembers.Keys.FirstOrDefault(key => key.EndsWith(token.ValueText));
                if (guessedLookupName == null)
                {
                    return "UNKNOWNSYMBOL::" + token.ValueText;
                }
                return guessedLookupName;
            }
            else
            {
                return symbol.ToDisplayString() + "::" + token.ValueText;
            }
        }

        private string GetNewNameForNamespace(NameSyntax nameSyntax)
        {
            if (renamedNamespaces.ContainsKey(nameSyntax))
            {
                return renamedNamespaces[nameSyntax];
            }

            //Just use the same namespace for everything for now
            string newNamespaceName = ObfuscatedNamespace;
            renamedNamespaces.Add(nameSyntax, newNamespaceName);
            return newNamespaceName;
        }
        private string GetNewNameForTokenAndSymbol(SyntaxToken token, ISymbol symbol, bool renameUnseenIdentifiers = true)
        {
            Dictionary<string, string> relevantDictionary = null;

            string symbolTokenLookupKey = GetSymbolTokenLookupKey(token, symbol);

            if (token.Parent.IsKind(SyntaxKind.PropertyDeclaration) ||
                token.Parent.IsKind(SyntaxKind.MethodDeclaration) ||
                token.Parent.IsKind(SyntaxKind.VariableDeclarator) ||
                token.Parent.IsKind(SyntaxKind.ClassDeclaration) ||
                token.Parent.IsKind(SyntaxKind.Parameter) ||
                token.Parent.IsKind(SyntaxKind.IdentifierName) && token.Parent.Parent.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                relevantDictionary = renamedMembers;
            }

            //Don't mess with external functions
            if (symbol != null && symbol.IsExtern)
            {
                return token.ValueText;
            }

            if (relevantDictionary != null)
            {
                if (relevantDictionary.ContainsKey(symbolTokenLookupKey))
                {
                    return relevantDictionary[symbolTokenLookupKey];
                }
                string newName = PolymorphicGenerator.GetRandomIdentifier(polymorphicCodeOptions);
                relevantDictionary.Add(symbolTokenLookupKey, newName);
                return newName;
            }
            
            if (renamedMembers.ContainsKey(symbolTokenLookupKey))
            {
                return renamedMembers[symbolTokenLookupKey];
            }

            if (renameUnseenIdentifiers)
            {
                //If this is just some random variable name that won't be accessed by another file
                //we don't need to track the change
                return PolymorphicGenerator.GetRandomIdentifier(polymorphicCodeOptions);
            }
            else
            {
                //If we're not renaming unseen identifiers, then just return the original value
                return token.ValueText;
            }
        }


        public SyntaxTree ObfuscateTypeReferences(SyntaxTree syntaxTree, Compilation compilation, List<Type> typesToObfuscate)
        {
            List<TextChange> changes = new List<TextChange>();

            foreach (Type type in typesToObfuscate)
            {
                var matchingTokens =
                    syntaxTree.GetRoot().DescendantTokens().Where(token => token.Text.Contains(type.Name)).ToList();


                var affectedNodes =
                    matchingTokens.Select(token => token.Parent).ToList();

                SemanticModel model = compilation.GetSemanticModel(syntaxTree);

                var affectedSymbols = affectedNodes.Select(node => model.GetSymbolInfo(node)).ToList();

                List<SyntaxNode> nodesToObfuscate = new List<SyntaxNode>();

                for (int index = 0; index < affectedNodes.Count; index += 1)
                {
                    if (affectedSymbols[index].Symbol != null &&
                        affectedSymbols[index].Symbol.ToDisplayString().Equals(type.FullName))
                    {
                        nodesToObfuscate.Add(affectedNodes[index]);
                    }
                }


                List<SyntaxNode> parents = nodesToObfuscate.Select(node => node.Parent).Distinct().ToList();

                //If we define something that implements IDisposable, make sure it's typed as that instead in case
                //we're used inside a Using statement
                bool typeIsDisposable = type.GetInterfaces().Contains(typeof(IDisposable));

                List<SyntaxToken> affectedVariables = new List<SyntaxToken>();

                //Apply changes to the following cases
                //TargetType variable = new TargetType(constructorArgs);
                foreach (var parent in parents)
                {
                    
                    //var variable = new TargetType(constructorArgs);
                    if (parent is VariableDeclarationSyntax vd)
                    {
                        string replacementTypeString = "var";

                        if (typeIsDisposable)
                        {
                            replacementTypeString = "IDisposable";
                        }

                        //The variable name used here might be used to access functions - we need to 
                        //generalize how those are accessed as well
                        SyntaxToken varNameSyntaxToken = vd.Variables.First().Identifier;
                        affectedVariables.Add(varNameSyntaxToken);

                        // Console.WriteLine("Replacing {0} with {1}", vd.Type.ToString(), replacementTypeString);
                        changes.Add(new TextChange(vd.Type.Span, replacementTypeString));
                    }
                    //TargetType variable = IDOL.InitializeTypeWithArgs(IDOL.GetTypeFromString("System.IO.Compression.GZipStream"), new object[] {constructorArgs});
                    else if (parent is ObjectCreationExpressionSyntax oce)
                    {
                        string indirectLoadFormatString =
                            "IndirectObjectLoader.InitializeTypeWithArgs(IndirectObjectLoader.GetTypeFromString(\"{0}\"),new object[] {{{1}}})";

                        if (typeIsDisposable)
                        {
                            indirectLoadFormatString = "(IDisposable)(" + indirectLoadFormatString + ")";
                        }

                        string indirectLoadString = string.Format(indirectLoadFormatString, type.FullName,
                            oce.ArgumentList.Arguments.ToFullString());
                        // Console.WriteLine("Replacing {0} with {1}", oce.ToString(), indirectLoadString);
                        changes.Add(new TextChange(oce.Span, indirectLoadString));
                    }
                }

                foreach (var affectedVariable in affectedVariables)
                {
                    List<TextChange> variableChanges =
                        CodeModificationHelper.GeneralizeIndentifierMethodInvocations(syntaxTree, affectedVariable,
                            type);
                    changes = changes.Concat(variableChanges).ToList();
                }
            }

            SourceText newSourceText = syntaxTree.GetText();
            newSourceText = newSourceText.WithChanges(changes);
            SyntaxTree newTree = syntaxTree.WithChangedText(newSourceText);

            newTree = InjectClassIntoTree(newTree, InjectableClasses.IndirectObjectLoader);
            return newTree;
        }

        public Compilation ObfuscateStringConstants(Compilation compilation)
        {
            List<SyntaxTree> oldTrees = compilation.SyntaxTrees.ToList();

            bool injectedEncryptor = false;

            foreach (SyntaxTree tree in oldTrees)
            {
                //We can obfuscate AssemblyInfo.cs later
                if (tree.FilePath.Contains("AssemblyInfo.cs"))
                {
                    continue;
                }

                SyntaxTree oldTree = tree;

                SyntaxTree newTree = ObfuscateStringConstants(tree, !injectedEncryptor);

                //After the first tree is obfuscated, we inject in the string encryptor
                injectedEncryptor = true;

                compilation = compilation.ReplaceSyntaxTree(oldTree, newTree);
            }

            return compilation;
        }

        public SyntaxTree ObfuscateStringConstants(SyntaxTree syntaxTree, bool injectStringEncryptor = true)
        {
            string replacementFormatString = "StringEncryptor.DecryptString(\"{0}\")";

            List<TextChange> stringEncryptorChanges =
                (from token in syntaxTree.GetRoot().DescendantTokens()
                where token.IsKind(SyntaxKind.StringLiteralToken) && token.Parent.Parent.Kind() != SyntaxKind.AttributeArgument
                select new TextChange(token.Span,
                    string.Format(replacementFormatString, StringEncryptor.EncryptString(token.ValueText)))).ToList();

            SourceText newSourceText = syntaxTree.GetText()
                .WithChanges(stringEncryptorChanges);

            SyntaxTree treeWithEncryptedStrings = syntaxTree.WithChangedText(newSourceText);

            if (injectStringEncryptor)
            {
                treeWithEncryptedStrings = InjectClassIntoTree(treeWithEncryptedStrings, InjectableClasses.StringEncryptor);
            }

            return treeWithEncryptedStrings;
        }

        private Compilation InjectClassIntoCompilation(Compilation compilation, InjectableClasses classToInject)
        {
            switch (classToInject)
            {
                case InjectableClasses.Properties:
                    string injectedSourceText = InjectedClassHelper.GetInjectableClassSourceText(classToInject);
                    injectedSourceText = injectedSourceText.Replace("REPLACEME", compilation.AssemblyName);
                    string methodFormatString = "internal static UnmanagedMemoryStream {0} {{ get {{ return Resources.ResourceManager.GetStream(\"{0}\", Resources.resourceCulture); }} }}\n";
                    string embeddedResourceMethods = "";
                    foreach (var embeddedResource in assemblyResources)
                    {
                        embeddedResourceMethods += string.Format(methodFormatString, embeddedResource.Name, embeddedResource.Name);
                    }

                    injectedSourceText =
                        injectedSourceText.Replace("/**EMBEDDEDRESOURCESHERE**/", embeddedResourceMethods);

                    LanguageVersion versionInUse =
                        ((CSharpParseOptions) compilation.SyntaxTrees.First().Options).LanguageVersion;

                    //Make sure we stay consistent with other parse versions in the tree
                    CSharpParseOptions parseOptions = new CSharpParseOptions().WithLanguageVersion(versionInUse);

                    SyntaxTree propertiesTree = CSharpSyntaxTree.ParseText(injectedSourceText, parseOptions);

                    var version = ((CSharpParseOptions)propertiesTree.Options).LanguageVersion;

                    compilation = compilation.AddSyntaxTrees(propertiesTree);
                    return compilation;
                default:
                    throw new NotImplementedException();
            }
        }

        private SyntaxTree InjectClassIntoTree(SyntaxTree syntaxTree, InjectableClasses classToInject)
        {
            string injectedSourceText = InjectedClassHelper.GetInjectableClassSourceText(classToInject);

            if (classToInject == InjectableClasses.StringEncryptor)
            {
                injectedSourceText = injectedSourceText.Replace("RANDOMIZEME", StringEncryptor.Key);
            }

            SyntaxTree injectedTree = CSharpSyntaxTree.ParseText(injectedSourceText);

            ClassDeclarationSyntax injectedClass =
                CodeIntrospectionHelper.GetFirstClassDeclarationFromSyntaxTree(injectedTree);

            SyntaxTree modifiedTree = CodeModificationHelper.InsertClassDeclarationIntoSyntaxTree(syntaxTree, injectedClass);
            modifiedTree = CodeModificationHelper.AddImportsToSyntaxTree(modifiedTree, injectedTree);

            return modifiedTree;
        }

        delegate SyntaxTree DObfuscateTreeWithInjectedClass(SyntaxTree tree, bool injectRelevantHelper);
        delegate SyntaxTree DObfuscateTree(SyntaxTree tree);

        public Compilation ObfuscateCompilation(Compilation compilation, ObfuscationType obfuscationType)
        {
            DObfuscateTreeWithInjectedClass obfuscateAndInjectTreeDelegate = null;
            DObfuscateTree obfuscateTreeDelegate = null;

            switch (obfuscationType)
            {
                case ObfuscationType.PInvokeObfuscation:
                    obfuscateAndInjectTreeDelegate = ObfuscatePInvokeCalls;
                    break;
                case ObfuscationType.EmbeddedResourceObfuscation:
                    obfuscateAndInjectTreeDelegate = HideLongStringLiteralsInResource;
                    break;
                case ObfuscationType.NamespaceObfuscation:
                    obfuscateTreeDelegate = ObfuscateNamespaces;
                    break;
                default:
                    throw new NotImplementedException();
            }

            List<SyntaxTree> oldTrees = compilation.SyntaxTrees.ToList();

            bool injectedClass = false;

            foreach (SyntaxTree tree in oldTrees)
            {
                SyntaxTree oldTree = tree;
                SyntaxTree newTree;
                if (obfuscateAndInjectTreeDelegate != null)
                {
                    newTree = obfuscateAndInjectTreeDelegate(tree, !injectedClass);
                }
                else
                {
                    newTree = obfuscateTreeDelegate(tree);
                }
                    
                injectedClass = true;
                compilation = compilation.ReplaceSyntaxTree(oldTree, newTree);
            }

            return compilation;
        }

        public Compilation HideLongStringLiteralsInResource(Compilation compilation)
        {
            return ObfuscateCompilation(compilation, ObfuscationType.EmbeddedResourceObfuscation);
        }

        public SyntaxTree HideLongStringLiteralsInResource(SyntaxTree syntaxTree, bool injectStegoHelper = true)
        {
            List<SyntaxToken> longStringLiteralTokens =
                (from token in syntaxTree.GetRoot().DescendantTokens()
                where token.IsKind(SyntaxKind.StringLiteralToken) && token.ValueText.Length > 1000
                select token).ToList();

            SyntaxTree newSyntaxTree = syntaxTree;
            List<TextChange> changes = new List<TextChange>();

            foreach (SyntaxToken stringLiteralToken in longStringLiteralTokens)
            {
                string longStringVal = stringLiteralToken.ValueText;
                byte[] payload = Encoding.UTF8.GetBytes(longStringVal);
                byte[] garbageWav = AudioSteganography.GenerateGarbageWAVFileForPayload(payload);
                string resourceName = PolymorphicGenerator.GetRandomIdentifier(polymorphicCodeOptions);

                EmbeddedResourceData embeddedData = new EmbeddedResourceData()
                {
                    Data = garbageWav,
                    Name = resourceName
                };

                assemblyResources.Add(embeddedData);

                string longLoadFormatString =
                    "StegoResourceLoader.GetPayloadFromWavFile(StegoResourceLoader.GetResourceBytes(\"{0}\"))";

                changes.Add(new TextChange(stringLiteralToken.Span, string.Format(longLoadFormatString, resourceName)));
            }

            SourceText newText = syntaxTree.GetText().WithChanges(changes);
            newSyntaxTree = syntaxTree.WithChangedText(newText);

            if (injectStegoHelper)
            {
                newSyntaxTree = InjectClassIntoTree(newSyntaxTree, InjectableClasses.StegoResourceLoader);
            }

            return newSyntaxTree;

        }

        public Compilation ObfuscateIdentifiers(Compilation compilation)
        {
            List<SyntaxTree> trees = compilation.SyntaxTrees.ToList();
            Dictionary<SyntaxTree, List<TextChange>> treeChanges = new Dictionary<SyntaxTree, List<TextChange>>();
            //First replace all the identifiers in each file
            foreach (SyntaxTree tree in trees)
            {
                List<TextChange> changes = new List<TextChange>();
                List<SyntaxToken> userIdentifiers = CodeIntrospectionHelper.GetDeclarationIdentifiersFromTree(tree);
                changes = ObfuscateIdentifiers(tree, compilation, userIdentifiers);
                treeChanges.Add(tree, changes);
            }

            //Then we do a second pass and replace references to classes/properties/fields
            //in other files that have changed in pass 1
            foreach (SyntaxTree tree in trees)
            {
                List<TextChange> changes = treeChanges[tree];

                List<SyntaxToken> identifiersToPossiblyChange = 
                    CodeIntrospectionHelper.GetIdentifierUsagesFromTree(tree);

                changes = changes.Concat(
                    ObfuscateIdentifiers(tree, compilation, identifiersToPossiblyChange, false)
                    ).ToList();
                changes = changes.Distinct().ToList();

                treeChanges[tree] = changes;
            }

            //Apply the source changes from pass 1 + pass 2
            foreach (SyntaxTree tree in trees)
            {
                SyntaxTree oldTree = tree;
                
                List<TextChange> changes = treeChanges[tree];

                try
                {
                    SourceText changedText = tree.GetText().WithChanges(changes);
                    SyntaxTree newTree = tree.WithChangedText(changedText);
                    // Pulling this into its own functionality
                    // newTree = ObfuscateNamespaces(newTree);
                    compilation = compilation.ReplaceSyntaxTree(oldTree, newTree);
                }
                catch (ArgumentException arg)
                {
                    changes = changes.OrderBy(s => s.Span.Start).ToList();
                    List<TextChange> overlaps = changes.Where(c =>
                        changes.Any(cc =>
                            ((c.Span.Start >= cc.Span.Start && c.Span.Start <= cc.Span.End) ||
                             (c.Span.End <= cc.Span.End && c.Span.End >= cc.Span.End)) && cc != c)).ToList();

                    Console.WriteLine(overlaps);
                }
            }

            return compilation;
        }

        public SyntaxTree ObfuscateIdentifiers(SyntaxTree tree, Compilation compilation)
        {
            SyntaxTree returnTree = tree;
            List<SyntaxToken> userIdentifiers = CodeIntrospectionHelper.GetDeclarationIdentifiersFromTree(tree);
            List<TextChange> changes = ObfuscateIdentifiers(tree, compilation, userIdentifiers);
            SourceText changedText = returnTree.GetText().WithChanges(changes);
            returnTree = returnTree.WithChangedText(changedText);
            returnTree = ObfuscateNamespaces(returnTree);
            return returnTree;
        }

        private List<TextChange> ObfuscateIdentifiers(SyntaxTree tree, Compilation compilation, 
            List<SyntaxToken> identifiers, bool renameUnseenIdentifiers = true)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            List<TextChange> changes = new List<TextChange>();
            foreach (var identifier in identifiers)
            {
                //Don't rename our entry point function
                if (identifier.Text.Equals("Main") &&
                    identifier.Parent.IsKind(SyntaxKind.MethodDeclaration))
                {
                    continue;
                }

                //TODO remove this
                //Debugging section because I'm too lazy to use conditional breakpoints
                // if (identifier.Text.Equals("Instance"))
                // {
                //     string letsDig = "True";
                // }

                ISymbol symbol = CodeIntrospectionHelper.GetSymbolForToken(model, identifier);

                //Make sure we're only renaming symbols that are from the compilation assembly
                if (symbol != null)
                {
                    string targetAssemblyName = compilation.AssemblyName;
                    string targetNamespace = ObfuscatedNamespace;
                    IAssemblySymbol containingAssembly = symbol.ContainingAssembly;

                    //Check if the symbol being modified belongs to a different assembly
                    if (containingAssembly != null)
                    {
                        string assemblyName = containingAssembly.Identity.GetDisplayName();
                        if (!assemblyName.StartsWith(targetAssemblyName))
                        {
                            continue;
                        }
                    }

                    if (containingAssembly == null)
                    {
                        //Sometimes (such as when the symbol is System), there is no containing assembly
                        //In that case make sure the namespace matches
                        string assemblyDisplayName = symbol.ToDisplayString();
                        if (!assemblyDisplayName.StartsWith(targetNamespace))
                        {
                            continue;
                        }
                    }
                }

                IEnumerable<TextSpan> renameSpans = CodeModificationHelper.GetRenameSpans(model, identifier);

                if (renameSpans == null)
                {
                    //Happens when we encounter Static classes that aren't defined in projects,
                    //ex: Encoding.UTF8.GetString - the Encoding identifier matches search critera but
                    //doesn't match any symbols
                    continue;
                }

                renameSpans = renameSpans.OrderBy(s => s);
                string newName = GetNewNameForTokenAndSymbol(identifier, symbol, renameUnseenIdentifiers);


                changes = changes.Concat(renameSpans.Select(s => new TextChange(s, newName))).ToList();
            }

            changes = changes.Distinct().ToList();
            return changes;
        }

        public SyntaxTree ObfuscateNamespaces(SyntaxTree tree)
        {
            List<NameSyntax> namespaceNodes = CodeIntrospectionHelper.GetUserNamespacesFromTree(tree);

            List<TextChange> changes = new List<TextChange>();
            foreach (NameSyntax namespaceNode in namespaceNodes)
            {
                string newName = GetNewNameForNamespace(namespaceNode);
                changes.Add(new TextChange(namespaceNode.Span, newName));
            }

            SourceText newSourceText = tree.GetText().WithChanges(changes);
                
            SyntaxTree treeWithRandomizedNamespaces = tree.WithChangedText(newSourceText);

            return treeWithRandomizedNamespaces;
        }

        public Compilation ObfuscateNamespaces(Compilation compilation)
        {
            return ObfuscateCompilation(compilation, ObfuscationType.NamespaceObfuscation);
        }

        public Compilation ObfuscatePInvokeCalls(Compilation compilation)
        {
            return ObfuscateCompilation(compilation, ObfuscationType.PInvokeObfuscation);
        }
        public SyntaxTree ObfuscatePInvokeCalls(SyntaxTree syntaxTree, bool injectPinvokeLoader = true)
        {
            SyntaxTree newTree = syntaxTree;
            var methodDeclarationNodes= syntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>();
            var pinvokeNodes = methodDeclarationNodes.Where(method => method.AttributeLists.Count >= 1 && 
                                                                             method.AttributeLists.Any(attributeList => attributeList.Attributes.Any(attribute => attribute.ToFullString().ToLower().StartsWith("dllimport")))).ToList();
            List<TextChange> changes = new List<TextChange>();
            foreach (var pinvokeNode in pinvokeNodes)
            {
                string functionName = pinvokeNode.Identifier.ToString();
                string returnTypeString = pinvokeNode.ReturnType.ToString();
                List<string> paramStrings = new List<string>();
                foreach (var parameter in pinvokeNode.ParameterList.Parameters)
                {
                    //For now we ignore MarshalAs attributes
                    var pString = parameter.WithAttributeLists(new SyntaxList<AttributeListSyntax>()).ToString();
                    paramStrings.Add(pString);
                }

                string metadataString = CodeModificationHelper.GetMetadataStringFromPInvokeSyntaxNode(pinvokeNode);
                //Grab the modifiers string and remove the extern modifier
                string modifiers = pinvokeNode.Modifiers.ToString().Replace("extern", "");

                //public static returnType OldIdentifier(old, arguments, with, out marshal, removed)
                //{
                //  marshal = new MarshalObjectType();
                //  object[] args = new object[] {old, arguments, with, marshal, removed};
                //  var result = PInvokeLoader.Instance.InvokePInvokeFunction(signature, args);
                //  marshal = args[3];
                //  return result;
                //}
                string paramString = string.Join(", ", paramStrings);
                string paramsAsArgString = string.Join(", ",
                    paramStrings.Select(paramString => paramString.Split(' ').Last()));

                string postInvokeAssignments = "";
                string preInvokeInitializations = "";
                for (int index = 0; index < paramStrings.Count; index += 1)
                {
                    string parameter = paramStrings[index];
                    string[] paramTokens = parameter.Split(' ');
                    string modifierToken = paramTokens.First();
                    //The second to last token is the Type
                    string paramType = paramTokens[paramTokens.Length - 2];
                    bool hasModifierToken = paramTokens.Length > 2;
                    string paramName = paramTokens.Last();
                    if (hasModifierToken && (modifierToken.Equals("ref") || modifierToken.Equals("out")))
                    {
                        string paramAssignmentFormatString = "\t{0} = ({1})args[{2}];\n";
                        string paramAssignmentString = string.Format(paramAssignmentFormatString, paramName, paramType, index);
                        postInvokeAssignments += paramAssignmentString;
                    }

                    if (hasModifierToken && modifierToken.Equals("out"))
                    {
                        //We're assuming there is an empty constructor here - will need to do more reflection if not
                        string paramInitializationFormatString = "\t{0} = new {1}();\n";
                        string paramInitializer = string.Format(paramInitializationFormatString, paramName, paramType);
                        preInvokeInitializations += paramInitializer;
                    }
                    
                }

                string returnStatementFormatString = "return ({0})result;\n";
                string returnStatement = string.Format(returnStatementFormatString, returnTypeString);
                if (returnTypeString.ToLower().Equals("void"))
                {
                    returnStatement = "";
                }

                string pinvokeReplaceFormatString =
                    "{0} {1} {2}({3})\n{{\n{4}\tobject[] args = new object[] {{{5}}};\n\tvar result = PInvokeLoader.Instance.InvokePInvokeFunction(\"{6}\", args);\n{7}{8}}}";

                string codeReplacement = string.Format(pinvokeReplaceFormatString, modifiers.TrimEnd(), returnTypeString,
                    functionName, paramString, preInvokeInitializations, paramsAsArgString, metadataString, postInvokeAssignments, returnStatement);
                
               
                changes.Add(new TextChange(pinvokeNode.Span, codeReplacement));
            }

            SourceText newText = syntaxTree.GetText().WithChanges(changes);
            newTree = syntaxTree.WithChangedText(newText);

            if (injectPinvokeLoader)
            {
                newTree = InjectClassIntoTree(newTree, InjectableClasses.PInvokeLoader);
            }

            return newTree;
        }

        private SyntaxTree ObfuscateTree(SyntaxTree tree, ref Compilation compilation, ObfuscationType obfuscationType)
        {
            SyntaxTree oldTree = tree;
            switch (obfuscationType)
            {
                case ObfuscationType.NamespaceObfuscation:
                    tree = ObfuscateNamespaces(tree);
                    break;
                case ObfuscationType.PInvokeObfuscation:
                    tree = ObfuscatePInvokeCalls(tree);
                    break;
                case ObfuscationType.EmbeddedResourceObfuscation:
                    tree = HideLongStringLiteralsInResource(tree);
                    break;
                case ObfuscationType.TypeReflectionObfuscation:
                    tree = ObfuscateTypeReferences(tree, compilation, new List<Type> {typeof(GZipStream)});
                    break;
                case ObfuscationType.StringEncryption:
                    tree = ObfuscateStringConstants(tree);
                    break;
                case ObfuscationType.IdentifierObfuscation:
                    tree = ObfuscateIdentifiers(tree, compilation);
                    break;
                default:
                    throw new NotImplementedException();
            }

            compilation = compilation.ReplaceSyntaxTree(oldTree, tree);
            return tree;
        }

        public SyntaxTree Obfuscate(SyntaxTree syntaxTree, Compilation compilation)
        {
            syntaxTree = ObfuscateTree(syntaxTree, ref compilation, ObfuscationType.PInvokeObfuscation);
            syntaxTree = ObfuscateTree(syntaxTree, ref compilation, ObfuscationType.TypeReflectionObfuscation);
            syntaxTree = ObfuscateTree(syntaxTree, ref compilation, ObfuscationType.EmbeddedResourceObfuscation);
            syntaxTree = ObfuscateTree(syntaxTree, ref compilation, ObfuscationType.StringEncryption);
            syntaxTree = ObfuscateTree(syntaxTree, ref compilation, ObfuscationType.NamespaceObfuscation);
            syntaxTree = ObfuscateTree(syntaxTree, ref compilation, ObfuscationType.IdentifierObfuscation);

            // SyntaxTree oldTree = syntaxTree;
            // syntaxTree = ObfuscateTypeReferences(syntaxTree,  compilation, new List<Type>() {typeof(GZipStream)});
            // compilation = compilation.ReplaceSyntaxTree(oldTree, syntaxTree);
            //
            // oldTree = syntaxTree;
            // syntaxTree = ObfuscateStringConstants(syntaxTree);
            // compilation = compilation.ReplaceSyntaxTree(oldTree, syntaxTree);
            //
            // oldTree = syntaxTree;
            // syntaxTree = ObfuscateNamespaces(syntaxTree);
            // compilation = compilation.ReplaceSyntaxTree(oldTree, syntaxTree);
            //
            // oldTree = syntaxTree;
            // syntaxTree = ObfuscateIdentifiers(syntaxTree, compilation);

            return syntaxTree;
        }

        public Compilation Obfuscate(Compilation compilation)
        {
            compilation = ObfuscatePInvokeCalls(compilation);
            compilation = HideLongStringLiteralsInResource(compilation);
            compilation = ObfuscateStringConstants(compilation);
            compilation = ObfuscateNamespaces(compilation);
            compilation = ObfuscateIdentifiers(compilation);
            return compilation;
        }

        public bool EmitAssembly(Compilation compilation, string filePath)
        {
            ResourceDescription rd = null;

            if (assemblyResources.Count > 0)
            {
                compilation = InjectClassIntoCompilation(compilation, InjectableClasses.Properties);

                string generatedResxFilePath = ResourceFileHelper.CreateResXFromEmbeddedResourceData(assemblyResources);
                Stream resourceStream = ResourceFileHelper.ReadResXFileAsMemoryStream(generatedResxFilePath);

                string resourceName = string.Format("{0}.Properties.Resources.resources", compilation.AssemblyName);

                rd = new ResourceDescription(resourceName,
                    () => resourceStream,
                    true);
            }

            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                string treeString = tree.ToString();
                // Console.WriteLine(treeString);
            }

            //For now we output 64 bit binaries
            CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithPlatform(Platform.X64).WithAllowUnsafe(true); 

            compilation = compilation.WithOptions(compilationOptions);
            EmitResult result;
            if (rd == null)
            {
                result = compilation.Emit(filePath);
            }
            else
            {
                result = compilation.Emit(filePath, manifestResources: new List<ResourceDescription>() { rd });
            }

            if (!result.Success)
            {
                throw new Exception("Emit Failed");
            }

            return true;
        }




    }
}
