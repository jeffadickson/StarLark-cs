@@ Comments inside the large functions of expr.star: the compiler (shunting-yard) and the evaluator.
@file apps/Basic/scripts/expr.star

@line out = []
The output (the compiled RPN list), the operator stack, and one argument counter per open "(" that belongs to a function call or array.

@line #103 expect = True
`expect` is True when an operand (a value, "(", a function, NOT or a minus sign) must come next, and False after an operand, when an operator
or a closing bracket must. It is what tells unary minus from binary minus, and catches errors like "3 4" and "3 +".

@line n = len(toks)
The loop is bounded by the number of tokens (each turn consumes at least one) because Starlark has no `while`.

@line if kind == "n" or kind == "s":
A number or a string: push it as a constant. Two operands in a row means the expression has ended, so stop and let the caller deal with the rest.

@line elif kind == "v":
A variable. Followed by "(" it is an array element: mark the open bracket on the operator stack so the subscripts are gathered, and count
its first argument. Otherwise it is a plain variable.

@line elif kind == "k":
A keyword: a built-in function, FN (a user function), NOT, AND or OR. Any other keyword (THEN, TO, STEP, ELSE...) ends the expression.

@line elif w == "NOT":
NOT is a prefix operator and applies to what follows it, so it just goes on the operator stack.

@line elif w == "AND" or w == "OR":
Binary operators: first move every operator already on the stack that binds at least as tightly into the output, then push this one.

@line elif kind == "o":
An operator or punctuation mark.

@line elif op == ")":
A closing bracket: move operators to the output back to the matching "(" and drop it. If that bracket belonged to a function, array or FN,
emit that item with its argument count.

@line elif op == ",":
An argument separator inside a function call or subscript list: finish the argument (flush its operators) and count the next one.

@line elif expect and (op == "-" or op == "+"):
A sign where an operand is expected. A minus becomes the unary operator; a plus does nothing.

@line elif op in BINARY:
An ordinary binary operator: pop everything that binds tighter or equally, then push this one.

@line if expect or depth != 0:
Ended in the middle of an expression (an operator or "(" still open): a syntax error.

@line #241 for _ in range(len(ops)):
End of the expression: move the remaining operators to the output.

@def eval_rpn
Evaluate compiled RPN with an explicit value stack. A call to a user function (DEF FN) cannot recurse here, so it saves the caller's code position
and stack in `frames`, binds the parameters as ordinary variables (remembering the values they hid), and runs the function body in the same loop;
when the body finishes, the frame is popped and the hidden values are put back. Returns (value, error).

@line for _ in range(100000000):
A bound, not a limit anyone should reach: Starlark has no `while`, and each turn takes one item.

@line if pc >= len(cur):
Reached the end of the current code. Either the whole expression is done (no frames left), or a user function has finished: restore the
caller's variables, code position and stack, and push the function's value for the caller to use.

@line elif tag == "B":
A binary operator: its two operands are the top of the stack (right operand on top).

@line elif tag == "call":
A built-in function: take its arguments off the stack, in order, and call it.

@line elif tag == "arr":
An array element: take the subscripts off the stack and look up the element (creating a 10-element array on first use, as BASIC does).

@line defn = st["fns"].get(item[1])
Anything else is a call to a DEF FN function.

@line if len(frames) >= MAX_FN_DEPTH:
Functions calling functions are limited to 30 deep ("OM"), since each level costs a frame.

@line saved = []
The values the parameters hide are saved so they can be restored afterwards. None means the variable did not exist.

@line frames.append((cur, pc, stack, saved))
Remember where to come back to, then switch to running the function's body.
