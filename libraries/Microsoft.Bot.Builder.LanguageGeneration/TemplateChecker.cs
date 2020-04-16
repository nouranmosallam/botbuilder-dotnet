// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AdaptiveExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;

namespace Microsoft.Bot.Builder.LanguageGeneration
{
    /// <summary>
    /// LG managed code checker.
    /// </summary>
    internal class TemplateChecker : LGTemplateParserBaseVisitor<List<Diagnostic>>
    {
        /// <summary>
        /// option regex.
        /// </summary>
        public static readonly Regex OptionRegex = new Regex(@"> *!#(.*)");

        /// <summary>
        /// Import regex.
        /// </summary>
        public static readonly Regex ImportRegex = new Regex(@"\[(.*)\]\((.*)\)");

        private static readonly Regex IdentifierRegex = new Regex(@"^[0-9a-zA-Z_]+$");

        private readonly ExpressionParser baseExpressionParser;
        private readonly Templates templates;
        private Template template;

        private IExpressionParser _expressionParser;

        /// <summary>
        /// Initializes a new instance of the <see cref="TemplateChecker"/> class.
        /// </summary>
        /// <param name="lg">the lg.</param>
        /// <param name="template">the template wihch would be checked.</param>
        public TemplateChecker(Templates lg, Template template)
        {
            this.template = template;
            this.templates = lg;
            baseExpressionParser = lg.ExpressionParser;
        }

        // Create a property because we want this to be lazy loaded
        private IExpressionParser ExpressionParser
        {
            get
            {
                if (_expressionParser == null)
                {
                    // create an evaluator to leverage it's customized function look up for checking
                    var evaluator = new Evaluator(templates.AllTemplates.ToList(), baseExpressionParser);
                    _expressionParser = evaluator.ExpressionParser;
                }

                return _expressionParser;
            }
        }

        /// <summary>
        /// Return error messages list.
        /// </summary>
        /// <returns>report result.</returns>
        public List<Diagnostic> Check()
        {
            var diagnostics = new List<Diagnostic>();

            var templateContext = template.SourceRange.ParseTree as LGFileParser.TemplateDefinitionContext;

            if (this.templates.Any(u => u.Name == template.Name))
            {
                var diagnostic = BuildTemplatesDiagnostic(TemplateErrors.DuplicatedTemplateInSameTemplate(template.Name), templateContext.templateNameLine());
                this.templates.Diagnostics.Add(diagnostic);
            }

            // checker duplicated in different files
            foreach (var reference in templates.References)
            {
                var sameTemplates = reference.Where(u => u.Name == template.Name);
                foreach (var sameTemplate in sameTemplates)
                {
                    var startLine = template.SourceRange.Range.Start.Line;
                    var range = new Range(startLine, 0, startLine, template.Name.Length + 1);
                    var diagnostic = new Diagnostic(range, TemplateErrors.DuplicatedTemplateInDiffTemplate(sameTemplate.Name, sameTemplate.SourceRange.Source), source: templates.Id);
                    diagnostics.Add(diagnostic);
                }
            }

            CheckTemplateName(template.Name, templateContext.templateNameLine());
            CheckTemplateParameters(template.Parameters, templateContext.templateNameLine());
            template.TemplateBodyParseTree = CheckTemplateBody(template.Name, template.Body, templateContext.templateBody(), template.SourceRange.Range.Start.Line);

            if (diagnostics.Count == 0 && template.TemplateBodyParseTree != null)
            {
                diagnostics.AddRange(Visit(template.TemplateBodyParseTree));
            }

            return diagnostics;
        }

        public override List<Diagnostic> VisitNormalTemplateBody([NotNull] LGTemplateParser.NormalTemplateBodyContext context)
        {
            var result = new List<Diagnostic>();

            foreach (var templateStr in context.templateString())
            {
                var errorTemplateStr = templateStr.errorTemplateString();
                if (errorTemplateStr != null)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.InvalidTemplateBody, context: errorTemplateStr));
                }
                else
                {
                    result.AddRange(Visit(templateStr.normalTemplateString()));
                }
            }

            return result;
        }

        public override List<Diagnostic> VisitStructuredTemplateBody([NotNull] LGTemplateParser.StructuredTemplateBodyContext context)
        {
            var result = new List<Diagnostic>();

            if (context.structuredBodyNameLine().errorStructuredName() != null)
            {
                result.Add(BuildLGDiagnostic(TemplateErrors.InvalidStrucName, context: context.structuredBodyNameLine()));
            }

            if (context.structuredBodyEndLine() == null)
            {
                result.Add(BuildLGDiagnostic(TemplateErrors.MissingStrucEnd, context: context));
            }

            var errors = context.errorStructureLine();
            if (errors != null && errors.Length > 0)
            {
                foreach (var error in errors)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.InvalidStrucBody, context: error));
                }
            }
            else
            {
                var bodys = context.structuredBodyContentLine();

                if (bodys == null || bodys.Length == 0)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.EmptyStrucContent, context: context));
                }
                else
                {
                    foreach (var body in bodys)
                    {
                        if (body.objectStructureLine() != null)
                        {
                            result.AddRange(CheckExpression(body.objectStructureLine().GetText(), body.objectStructureLine()));
                        }
                        else
                        {
                            // KeyValueStructuredLine
                            var structureValues = body.keyValueStructureLine().keyValueStructureValue();
                            var errorPrefix = "Property '" + body.keyValueStructureLine().STRUCTURE_IDENTIFIER().GetText() + "':";
                            foreach (var structureValue in structureValues)
                            {
                                foreach (var expression in structureValue.EXPRESSION_IN_STRUCTURE_BODY())
                                {
                                    result.AddRange(CheckExpression(expression.GetText(), structureValue, errorPrefix));
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        public override List<Diagnostic> VisitIfElseBody([NotNull] LGTemplateParser.IfElseBodyContext context)
        {
            var result = new List<Diagnostic>();

            var ifRules = context.ifElseTemplateBody().ifConditionRule();
            for (var idx = 0; idx < ifRules.Length; idx++)
            {
                var conditionNode = ifRules[idx].ifCondition();
                var ifExpr = conditionNode.IF() != null;
                var elseIfExpr = conditionNode.ELSEIF() != null;
                var elseExpr = conditionNode.ELSE() != null;

                var node = ifExpr ? conditionNode.IF() :
                           elseIfExpr ? conditionNode.ELSEIF() :
                           conditionNode.ELSE();

                if (node.GetText().Count(u => u == ' ') > 1)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.InvalidWhitespaceInCondition, context: conditionNode));
                }

                if (idx == 0 && !ifExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.NotStartWithIfInCondition, DiagnosticSeverity.Warning, conditionNode));
                }

                if (idx > 0 && ifExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.MultipleIfInCondition, context: conditionNode));
                }

                if (idx == ifRules.Length - 1 && !elseExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.NotEndWithElseInCondition, DiagnosticSeverity.Warning, conditionNode));
                }

                if (idx > 0 && idx < ifRules.Length - 1 && !elseIfExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.InvalidMiddleInCondition, context: conditionNode));
                }

                // check rule should should with one and only expression
                if (!elseExpr)
                {
                    if (ifRules[idx].ifCondition().EXPRESSION().Length != 1)
                    {
                        result.Add(BuildLGDiagnostic(TemplateErrors.InvalidExpressionInCondition, context: conditionNode));
                    }
                    else
                    {
                        var errorPrefix = "Condition '" + conditionNode.EXPRESSION(0).GetText() + "': ";
                        result.AddRange(CheckExpression(conditionNode.EXPRESSION(0).GetText(), conditionNode, errorPrefix));
                    }
                }
                else
                {
                    if (ifRules[idx].ifCondition().EXPRESSION().Length != 0)
                    {
                        result.Add(BuildLGDiagnostic(TemplateErrors.ExtraExpressionInCondition, context: conditionNode));
                    }
                }

                if (ifRules[idx].normalTemplateBody() != null)
                {
                    result.AddRange(Visit(ifRules[idx].normalTemplateBody()));
                }
                else
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.MissingTemplateBodyInCondition, context: conditionNode));
                }
            }

            return result;
        }

        public override List<Diagnostic> VisitSwitchCaseBody([NotNull] LGTemplateParser.SwitchCaseBodyContext context)
        {
            var result = new List<Diagnostic>();
            var switchCaseRules = context.switchCaseTemplateBody().switchCaseRule();
            var length = switchCaseRules.Length;
            for (var idx = 0; idx < length; idx++)
            {
                var switchCaseNode = switchCaseRules[idx].switchCaseStat();
                var switchExpr = switchCaseNode.SWITCH() != null;
                var caseExpr = switchCaseNode.CASE() != null;
                var defaultExpr = switchCaseNode.DEFAULT() != null;
                var node = switchExpr ? switchCaseNode.SWITCH() :
                           caseExpr ? switchCaseNode.CASE() :
                           switchCaseNode.DEFAULT();

                if (node.GetText().Count(u => u == ' ') > 1)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.InvalidWhitespaceInSwitchCase, context: switchCaseNode));
                }

                if (idx == 0 && !switchExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.NotStartWithSwitchInSwitchCase, context: switchCaseNode));
                }

                if (idx > 0 && switchExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.MultipleSwithStatementInSwitchCase, context: switchCaseNode));
                }

                if (idx > 0 && idx < length - 1 && !caseExpr)
                {
                    result.Add(BuildLGDiagnostic(TemplateErrors.InvalidStatementInMiddlerOfSwitchCase, context: switchCaseNode));
                }

                if (idx == length - 1 && (caseExpr || defaultExpr))
                {
                    if (caseExpr)
                    {
                        result.Add(BuildLGDiagnostic(TemplateErrors.NotEndWithDefaultInSwitchCase, DiagnosticSeverity.Warning, switchCaseNode));
                    }
                    else
                    {
                        if (length == 2)
                        {
                            result.Add(BuildLGDiagnostic(TemplateErrors.MissingCaseInSwitchCase, DiagnosticSeverity.Warning, switchCaseNode));
                        }
                    }
                }

                if (switchExpr || caseExpr)
                {
                    if (switchCaseNode.EXPRESSION().Length != 1)
                    {
                        result.Add(BuildLGDiagnostic(TemplateErrors.InvalidExpressionInSwiathCase, context: switchCaseNode));
                    }
                    else
                    {
                        var errorPrefix = switchExpr ? "Switch" : "Case";
                        errorPrefix += " '" + switchCaseNode.EXPRESSION(0).GetText() + "': ";
                        result.AddRange(CheckExpression(switchCaseNode.EXPRESSION(0).GetText(), switchCaseNode, errorPrefix));
                    }
                }
                else
                {
                    if (switchCaseNode.EXPRESSION().Length != 0 || switchCaseNode.TEXT().Length != 0)
                    {
                        result.Add(BuildLGDiagnostic(TemplateErrors.ExtraExpressionInSwitchCase, context: switchCaseNode));
                    }
                }

                if (caseExpr || defaultExpr)
                {
                    if (switchCaseRules[idx].normalTemplateBody() != null)
                    {
                        result.AddRange(Visit(switchCaseRules[idx].normalTemplateBody()));
                    }
                    else
                    {
                        result.Add(BuildLGDiagnostic(TemplateErrors.MissingTemplateBodyInSwitchCase, context: switchCaseNode));
                    }
                }
            }

            return result;
        }

        public override List<Diagnostic> VisitNormalTemplateString([NotNull] LGTemplateParser.NormalTemplateStringContext context)
        {
            var prefixErrorMsg = context.GetPrefixErrorMessage();
            var result = new List<Diagnostic>();

            foreach (var expression in context.EXPRESSION())
            {
                result.AddRange(CheckExpression(expression.GetText(), context, prefixErrorMsg));
            }

            var multiLinePrefix = context.MULTILINE_PREFIX();
            var multiLineSuffix = context.MULTILINE_SUFFIX();

            if (multiLinePrefix != null && multiLineSuffix == null)
            {
                result.Add(BuildLGDiagnostic(TemplateErrors.NoEndingInMultiline, context: context));
            }

            return result;
        }

        private LGTemplateParser.TemplateBodyContext CheckTemplateBody(string templateName, string templateBody, LGFileParser.TemplateBodyContext context, int startLine)
        {
            if (string.IsNullOrWhiteSpace(templateBody))
            {
                var diagnostic = BuildTemplatesDiagnostic(TemplateErrors.NoTemplateBody(templateName), context, DiagnosticSeverity.Warning);
                this.templates.Diagnostics.Add(diagnostic);
            }
            else
            {
                try
                {
                    return AntlrParseTemplate(templateBody, startLine);
                }
                catch (TemplateException e)
                {
                    e.Diagnostics.ToList().ForEach(u => this.templates.Diagnostics.Add(u));
                }
            }

            return null;
        }

        private void CheckTemplateParameters(List<string> parameters, LGFileParser.TemplateNameLineContext context)
        {
            foreach (var parameter in parameters)
            {
                if (!IdentifierRegex.IsMatch(parameter))
                {
                    var diagnostic = BuildTemplatesDiagnostic(TemplateErrors.InvalidTemplateName, context);
                    this.templates.Diagnostics.Add(diagnostic);
                }
            }
        }

        private void CheckTemplateName(string templateName, ParserRuleContext context)
        {
            var functionNameSplitDot = templateName.Split('.');
            foreach (var id in functionNameSplitDot)
            {
                if (!IdentifierRegex.IsMatch(id))
                {
                    var diagnostic = BuildTemplatesDiagnostic(TemplateErrors.InvalidTemplateName, context);
                    this.templates.Diagnostics.Add(diagnostic);
                }
            }
        }

        private List<Diagnostic> CheckExpression(string exp, ParserRuleContext context, string prefix = "")
        {
            var result = new List<Diagnostic>();
            if (!exp.EndsWith("}"))
            {
                result.Add(BuildLGDiagnostic(TemplateErrors.NoCloseBracket, context: context));
            }
            else
            {
                exp = exp.TrimExpression();

                try
                {
                    ExpressionParser.Parse(exp);
                }
                catch (Exception e)
                {
                    var suffixErrorMsg = Evaluator.ConcatErrorMsg(TemplateErrors.ExpressionParseError(exp), e.Message);
                    var errorMsg = Evaluator.ConcatErrorMsg(prefix, suffixErrorMsg);

                    result.Add(BuildLGDiagnostic(errorMsg, context: context));
                    return result;
                }
            }

            return result;
        }

        /// <summary>
        /// Build LG diagnostic with ANTLR tree node context.
        /// </summary>
        /// <param name="message">error/warning message. <see cref="Diagnostic.Message"/>.</param>
        /// <param name="severity">diagnostic Severity <see cref="DiagnosticSeverity"/> to get more info.</param>
        /// <param name="context">the parsed tree node context of the diagnostic.</param>
        /// <returns>new Diagnostic object.</returns>
        private Diagnostic BuildLGDiagnostic(
            string message,
            DiagnosticSeverity severity = DiagnosticSeverity.Error,
            ParserRuleContext context = null)
        {
            var lineOffset = this.template != null ? this.template.SourceRange.Range.Start.Line : 0;
            message = this.template != null ? $"[{this.template.Name}]" + message : message;
            var range = context == null ? new Range(1 + lineOffset, 0, 1 + lineOffset, 0) : context.ConvertToRange(lineOffset);
            return new Diagnostic(range, message, severity, templates.Id);
        }

        private LGTemplateParser.TemplateBodyContext AntlrParseTemplate(string templateBody, int lineOffset)
        {
            var input = new AntlrInputStream(templateBody);
            var lexer = new LGTemplateLexer(input);
            lexer.RemoveErrorListeners();

            var tokens = new CommonTokenStream(lexer);
            var parser = new LGTemplateParser(tokens);
            parser.RemoveErrorListeners();
            var listener = new ErrorListener(this.templates.Id, lineOffset);

            parser.AddErrorListener(listener);
            parser.BuildParseTree = true;

            return parser.context().templateBody();
        }

        private Diagnostic BuildTemplatesDiagnostic(string errorMessage, ParserRuleContext context, DiagnosticSeverity severity = DiagnosticSeverity.Error)
        {
            return new Diagnostic(context.ConvertToRange(), errorMessage, severity, this.templates.Id);
        }
    }
}
