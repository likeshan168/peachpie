﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Semantics.Graph;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Pchp.CodeAnalysis.CodeGen;
using Pchp.CodeAnalysis.FlowAnalysis;
using Devsense.PHP.Syntax.Ast;
using Devsense.PHP.Syntax;
using Devsense.PHP.Text;
using System.Globalization;
using System.Threading;

namespace Pchp.CodeAnalysis.Symbols
{
    /// <summary>
    /// Base symbol representing a method or a function from source.
    /// </summary>
    internal abstract partial class SourceRoutineSymbol : MethodSymbol
    {
        ControlFlowGraph _cfg;
        LocalsTable _locals;

        /// <summary>
        /// Lazily bound semantic block.
        /// Entry point of analysis and emitting.
        /// </summary>
        public override ControlFlowGraph ControlFlowGraph
        {
            get
            {
                if (_cfg == null && this.Statements != null) // ~ Statements => non abstract method
                {
                    // create initial flow state
                    var state = StateBinder.CreateInitialState(this);

                    // build control flow graph
                    _cfg = new ControlFlowGraph(
                        this.Statements,
                        SemanticsBinder.Create(this.LocalsTable, DeclaringCompilation.DeclarationDiagnostics),
                        this.GetNamingContext());
                    _cfg.Start.FlowState = state;
                }

                return _cfg;
            }
        }

        /// <summary>
        /// Gets table of local variables.
        /// Variables are lazily added to the table.
        /// </summary>
        internal LocalsTable LocalsTable
        {
            get
            {
                var locals = _locals;
                if (locals == null)
                {
                    _locals = locals = new LocalsTable(this);
                }

                return locals;
            }
        }

        internal abstract IList<Statement> Statements { get; }

        protected abstract TypeRefContext CreateTypeRefContext();

        internal abstract Signature SyntaxSignature { get; }

        /// <summary>
        /// Specified return type.
        /// </summary>
        internal abstract TypeRef SyntaxReturnType { get; }

        /// <summary>
        /// Gets routine declaration syntax.
        /// </summary>
        internal abstract AstNode Syntax { get; }

        /// <summary>
        /// Optionaly gets routines PHP doc block.
        /// </summary>
        internal abstract PHPDocBlock PHPDocBlock { get; }

        /// <summary>
        /// Reference to a containing file symbol.
        /// </summary>
        internal abstract SourceFileSymbol ContainingFile { get; }

        protected List<ParameterSymbol> _implicitParams;
        private SourceParameterSymbol[] _srcParams;
        private SynthesizedParameterSymbol _varargParam;

        /// <summary>
        /// Builds implicit parameters before source parameters.
        /// </summary>
        /// <returns></returns>
        protected virtual IEnumerable<ParameterSymbol> BuildImplicitParams()
        {
            var index = 0;

            if (IsStatic)  // instance methods have <ctx> in <this>.<ctx> field, see SourceNamedTypeSymbol._lazyContextField
            {
                // Context <ctx>
                yield return new SpecialParameterSymbol(this, DeclaringCompilation.CoreTypes.Context, SpecialParameterSymbol.ContextName, index++);
            }
        }

        /// <summary>
        /// Gets value indicating this routine requires a special {PhpTypeInfo static} parameter to resolve `static` reserved type inside the routine body.
        /// </summary>
        internal virtual bool RequiresLateStaticBoundParam => false;

        /// <summary>
        /// Constructs routine source parameters.
        /// </summary>
        protected IEnumerable<SourceParameterSymbol> BuildSrcParams(IEnumerable<FormalParam> formalparams, PHPDocBlock phpdocOpt = null)
        {
            var pindex = 0;

            foreach (var p in formalparams)
            {
                var ptag = (phpdocOpt != null) ? PHPDoc.GetParamTag(phpdocOpt, pindex, p.Name.Name.Value) : null;

                yield return new SourceParameterSymbol(this, p, pindex++, ptag);
            }
        }

        protected virtual IEnumerable<SourceParameterSymbol> BuildSrcParams(Signature signature, PHPDocBlock phpdocOpt = null)
        {
            return BuildSrcParams(signature.FormalParams, phpdocOpt);
        }

        internal virtual List<ParameterSymbol> ImplicitParameters
        {
            get
            {
                if (_implicitParams == null)
                {
                    _implicitParams = BuildImplicitParams().ToList();
                }

                if (RequiresLateStaticBoundParam && !_implicitParams.Any(SpecialParameterSymbol.IsLateStaticParameter))
                {
                    // PhpTypeInfo <static>
                    _implicitParams.Add(new SpecialParameterSymbol(this, DeclaringCompilation.CoreTypes.PhpTypeInfo, SpecialParameterSymbol.StaticTypeName, _implicitParams.Count));
                }

                //
                return _implicitParams;
            }
        }

        internal SourceParameterSymbol[] SourceParameters
        {
            get
            {
                if (_srcParams == null)
                {
                    _srcParams = BuildSrcParams(this.SyntaxSignature, this.PHPDocBlock).ToArray();
                }

                return _srcParams;
            }
        }

        /// <summary>
        /// Implicitly added parameter corresponding to <c>params PhpValue[] {arguments}</c>.
        /// Can be <c>null</c> if not needed.
        /// </summary>
        protected ParameterSymbol VarargsParam
        {
            get
            {
                // declare implicit [... varargs] parameter if needed and not defined as source parameter

                if ((Flags & RoutineFlags.RequiresParams) != 0 && _varargParam == null && (this is SourceFunctionSymbol || this is SourceMethodSymbol || this is SourceLambdaSymbol))
                {
                    var srcparams = this.SourceParameters;
                    if (srcparams.Length == 0 || !srcparams.Last().IsParams)
                    {
                        _varargParam = new SynthesizedParameterSymbol( // IsImplicitlyDeclared, IsParams
                            this,
                            ArrayTypeSymbol.CreateSZArray(this.ContainingAssembly, this.DeclaringCompilation.CoreTypes.PhpValue),
                            srcparams.Length,
                            RefKind.None,
                            SpecialParameterSymbol.ParamsName, true);
                    }
                }

                if (_varargParam != null)
                {
                    _varargParam.UpdateOrdinal(ImplicitParameters.Count + SourceParameters.Length);
                }

                return _varargParam;
            }
        }

        /// <summary>
        /// Gets params parameter or null.
        /// </summary>
        internal ParameterSymbol GetParamsParameter()
        {
            var srcparams = this.SourceParameters;
            if (srcparams.Length != 0)
            {
                // explicitly declared via '...'
                var last = srcparams[srcparams.Length - 1];
                if (last.IsParams)
                {
                    Debug.Assert(last.Type.IsSZArray());
                    return last;
                }
            }

            // implicitly declared when needed by routine code
            return VarargsParam;
        }

        public override bool IsExtern => false;

        public override bool IsOverride => false;

        public override bool IsVirtual => !IsSealed && !IsStatic;

        public override bool CastToFalse => false;  // source routines never cast special values to FALSE

        public override MethodKind MethodKind
        {
            get
            {
                // TODO: ctor, dtor, props, magic, ...

                return MethodKind.Ordinary;
            }
        }

        public sealed override ImmutableArray<ParameterSymbol> Parameters
        {
            get
            {
                // [implicit parameters], [source parameters], [...varargs]

                var result = ImmutableArray<ParameterSymbol>.Empty;

                result = result.AddRange(ImplicitParameters.Concat(SourceParameters));

                var vararg = VarargsParam;
                if (vararg != null)
                {
                    result = result.Add(vararg);
                }

                return result;
            }
        }

        public sealed override int ParameterCount
        {
            get
            {
                // [implicit parameters], [source parameters], [...varargs]

                var ps1 = ImplicitParameters;
                var ps2 = SourceParameters;
                var vararg = (VarargsParam != null) ? 1 : 0;

                return ps1.Count + ps2.Length + vararg;
            }
        }

        public override bool ReturnsVoid => ReturnType.SpecialType == SpecialType.System_Void;

        public override TypeSymbol ReturnType => PhpRoutineSymbolExtensions.ConstructClrReturnType(this);

        internal override ObsoleteAttributeData ObsoleteAttributeData => null;   // TODO: from PHPDoc

        /// <summary>
        /// virtual = IsVirtual AND NewSlot 
        /// override = IsVirtual AND !NewSlot
        /// </summary>
        internal override bool IsMetadataNewSlot(bool ignoreInterfaceImplementationChanges = false) => !IsOverride && IsMetadataVirtual(ignoreInterfaceImplementationChanges);

        internal override bool IsMetadataVirtual(bool ignoreInterfaceImplementationChanges = false) => IsVirtual && (!ContainingType.IsSealed || IsOverride || IsAbstract); // do not make method virtual if not necessary

        public override string GetDocumentationCommentXml(CultureInfo preferredCulture = null, bool expandIncludes = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            // TODO: XmlDocumentationCommentCompiler
            return this.PHPDocBlock?.Summary ?? string.Empty;
        }
    }
}
