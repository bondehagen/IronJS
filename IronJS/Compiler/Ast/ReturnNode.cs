﻿using System;
using System.Text;
using Antlr.Runtime.Tree;
using IronJS.Runtime.Utils;
using IronJS.Runtime2.Js;
using Et = System.Linq.Expressions.Expression;
using IronJS.Compiler.Utils;

namespace IronJS.Compiler.Ast
{
    public class ReturnNode : Node
    {
        public INode Value { get; protected set; }
        public FuncNode FuncNode { get; protected set; }

        public ReturnNode(INode value, ITree node)
            : base(NodeType.Return, node)
        {
            Value = value;
        }

        public override INode Analyze(FuncNode func)
        {
            Value = Value.Analyze(func);

            FuncNode = func;
            FuncNode.Returns.Add(Value);

            return this;
        }

        public override Et EtGen(FuncNode func)
        {
            return Et.Return(
                func.ReturnLabel,
                IjsEtGenUtils.Box(
                    Value.EtGen(func)
                ),
                func.ReturnType
            );
        }

        public override void Print(StringBuilder writer, int indent = 0)
        {
            var indentStr = new String(' ', indent * 2);

            writer.AppendLine(indentStr + "(" + NodeType);

            if (Value != null)
                Value.Print(writer, indent + 1);

            writer.AppendLine(indentStr + ")");
        }
    }
}
