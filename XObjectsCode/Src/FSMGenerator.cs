//Copyright (c) Microsoft Corporation.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Xml.Schema;
using System.Xml.Linq;
using System.CodeDom;

namespace Xml.Schema.Linq.CodeGen
{
    public partial class GroupingInfo
    {
        public override FSM MakeFSM(StateNameSource stateNames)
        {
            FSM fsm = null;
            switch (this.contentModelType)
            {
                case ContentModelType.Sequence:
                    fsm = MakeSequenceFSM(stateNames);
                    break;
                case ContentModelType.Choice:
                    fsm = MakeChoiceFSM(stateNames);
                    break;
                default:
                    throw new InvalidOperationException();
            }

            return ImplementFSMCardinality(fsm, stateNames);
        }

        private FSM MakeSequenceFSM(StateNameSource stateNames)
        {
            FSM fsm = null;
            Set<int> fsmAccept = null;

            foreach (ContentInfo child in Children)
            {
                FSM currFsm = child.MakeFSM(stateNames);

                if (fsm == null)
                {
                    fsm = currFsm;
                    fsmAccept = currFsm.Accept;
                }
                else
                {
                    int currStart = currFsm.Start;
                    foreach (int oldFinalState in fsmAccept)
                    {
                        FSM.CloneTransitions(currFsm, currStart, fsm, oldFinalState);
                    }

                    fsm.AddTransitions(currFsm);
                    //clear old final states only if the initial state of currFsm is not a final state in currFsm
                    if (!currFsm.Accept.Contains(currStart)) fsmAccept.Clear();
                    Set<int> currAccept = currFsm.Accept;
                    foreach (int state in currAccept) fsmAccept.Add(state);
                }
            }

            return fsm;
        }

        private FSM MakeChoiceFSM(StateNameSource stateNames)
        {
            FSM fsm = null;
            int fsmStart = FSM.InvalidState;
            Set<int> fsmAccept = null;

            foreach (ContentInfo child in Children)
            {
                FSM currFsm = child.MakeFSM(stateNames);

                if (fsm == null)
                {
                    //first node
                    fsm = currFsm;
                    fsmStart = currFsm.Start;
                    fsmAccept = currFsm.Accept;
                }
                else
                {
                    //Merge the start states
                    FSM.CloneTransitions(currFsm, currFsm.Start, fsm, fsmStart);
                    //Copy other transitions
                    fsm.AddTransitions(currFsm);
                    //update final states
                    if (currFsm.isAccept(currFsm.Start)) fsmAccept.Add(fsmStart);
                    foreach (int state in currFsm.Accept) fsmAccept.Add(state);
                }
            }

            return fsm;
        }
    }

    public partial class ClrPropertyInfo : ClrBasePropertyInfo
    {
        public override FSM MakeFSM(StateNameSource stateNames)
        {
            //Create a simple fsm with (0,(schemaName,1),{1})
            Dictionary<int, Transitions> transitions = new Dictionary<int, Transitions>();
            int start = stateNames.Next();
            int end = stateNames.Next();
            Transitions trans = new Transitions();

            if (this.IsSubstitutionHead)
            {
                foreach (XmlSchemaElement element in SubstitutionMembers)
                {
                    trans.Add(XName.Get(element.QualifiedName.Name, element.QualifiedName.Namespace), end);
                }
            }
            else
            {
                trans.Add(XName.Get(schemaName, PropertyNs), end);
            }

            transitions.Add(start, trans);
            return ImplementFSMCardinality(new FSM(start, new Set<int>(end), transitions), stateNames);
        }
    }

    public partial class ClrWildCardPropertyInfo : ClrBasePropertyInfo
    {
        public override FSM MakeFSM(StateNameSource stateNames)
        {
            Dictionary<int, Transitions> transitions = new Dictionary<int, Transitions>();
            int start = stateNames.Next();
            int end = stateNames.Next();
            transitions.Add(start,
                new Transitions(new SingleTransition(new WildCard(this.Namespaces, this.TargetNamespace), end)));
            FSM fsm = new FSM(start, new Set<int>(end), transitions);

            return ImplementFSMCardinality(fsm, stateNames);
        }
    }

    public class StateNameSource
    {
        private int nextName = 1;

        public int Next()
        {
            return nextName++;
        }

        public void Reset()
        {
            nextName = 1;
        }
    }

    public class FSMCodeDomHelper
    {
        public static void CreateFSMStmt(FSM fsm, CodeStatementCollection stmts)
        {
            //First create: Dictionary<int, Transitions> transitions = new Dictionary<int,Transitions>();
            //Then create: transitions.Add(0, new Transitions(...));
            //Last: fsm = new DFA(start, new Set<int>(end), transitions);
            CodeTypeReference typeRef =
                CodeDomHelper.CreateGenericTypeReference("Dictionary", new string[] {Constants.Int, "Transitions"});
            stmts.Add(new CodeVariableDeclarationStatement(typeRef, Constants.TransitionsVar,
                new CodeObjectCreateExpression(typeRef, new CodeExpression[] { })));

            //Then add all transitions
            Set<int> visited = new Set<int>();
            AddTransitions(fsm, fsm.Start, stmts, visited);

            //Clean up accept states
            Set<int> reachableAccept = new Set<int>();
            foreach (int state in fsm.Accept)
            {
                if (visited.Contains(state)) reachableAccept.Add(state);
            }

            stmts.Add(new CodeAssignStatement(new CodeVariableReferenceExpression(Constants.FSMMember),
                new CodeObjectCreateExpression(new CodeTypeReference(Constants.FSMClass),
                    new CodePrimitiveExpression(fsm.Start),
                    CreateSetCreateExpression(reachableAccept),
                    new CodeVariableReferenceExpression(Constants.TransitionsVar))));
        }

        public static void AddTransitions(FSM fsm, int state, CodeStatementCollection stmts, Set<int> visited)
        {
            if (visited.Contains(state)) return;
            else visited.Add(state);

            Transitions currTrans = null;
            fsm.Trans.TryGetValue(state, out currTrans);
            if (currTrans == null || currTrans.Count == 0) return;

            CreateAddTransitionStmts(fsm, stmts, state, currTrans, visited);
        }

        public static void CreateAddTransitionStmts(FSM fsm,
            CodeStatementCollection stmts,
            int state,
            Transitions currTrans,
            Set<int> visited)
        {
            Set<int> subStates = new Set<int>();
            CodeExpression[] initializers = new CodeExpression[currTrans.Count];

            int index = 0;
            if (currTrans.nameTransitions != null)
                foreach (KeyValuePair<XName, int> s1Trans in currTrans.nameTransitions)
                {
                    initializers[index++] = CreateSingleTransitionExpr(CreateXNameExpr(s1Trans.Key), s1Trans.Value);
                    subStates.Add(s1Trans.Value);
                }

            if (currTrans.wildCardTransitions != null)
                foreach (KeyValuePair<WildCard, int> s1Trans in currTrans.wildCardTransitions)
                {
                    initializers[index++] = CreateSingleTransitionExpr(CreateWildCardExpr(s1Trans.Key), s1Trans.Value);
                    subStates.Add(s1Trans.Value);
                }


            stmts.Add(CodeDomHelper.CreateMethodCall(new CodeVariableReferenceExpression(Constants.TransitionsVar),
                "Add",
                new CodeExpression[]
                {
                    new CodePrimitiveExpression(state),
                    new CodeObjectCreateExpression("Transitions", initializers)
                }));

            //Recursively call AddTransitions on subsequent states
            foreach (int s in subStates) AddTransitions(fsm, s, stmts, visited);
        }


        public static CodeExpression CreateSingleTransitionExpr(CodeExpression labelExpr, int nextState)
        {
            return new CodeObjectCreateExpression(
                Constants.SingleTrans,
                labelExpr,
                new CodePrimitiveExpression(nextState));
        }

        public static CodeExpression CreateXNameExpr(XName name)
        {
            return CodeDomHelper.CreateMethodCall(new CodeTypeReferenceExpression(Constants.XNameType),
                "Get",
                new CodeExpression[]
                {
                    new CodePrimitiveExpression(name.LocalName),
                    new CodePrimitiveExpression(name.Namespace.NamespaceName)
                });
        }

        public static CodeExpression CreateWildCardExpr(WildCard any)
        {
            return new CodeObjectCreateExpression(
                Constants.WildCard,
                new CodeExpression[]
                {
                    new CodePrimitiveExpression(any.NsList.Namespaces),
                    new CodePrimitiveExpression(any.NsList.TargetNamespace)
                }
            );
        }

        public static CodeObjectCreateExpression CreateSetCreateExpression(Set<int> set)
        {
            CodeObjectCreateExpression createSet =
                new CodeObjectCreateExpression(
                    CodeDomHelper.CreateGenericTypeReference("Set", new string[] {Constants.Int}));

            CodeExpressionCollection parameters = createSet.Parameters;
            if (set.Count == 1)
            {
                foreach (int i in set)
                {
                    parameters.Add(new CodePrimitiveExpression(i));
                }
            }
            else if (set.Count > 1)
            {
                CodeArrayCreateExpression array = new CodeArrayCreateExpression();
                array.CreateType = CodeDomHelper.CreateTypeReference(Constants.Int);

                CodeExpressionCollection initializers = array.Initializers;
                foreach (int i in set)
                {
                    initializers.Add(new CodePrimitiveExpression(i));
                }

                parameters.Add(array);
            }

            return createSet;
        }
    }
}