----------------------------- MODULE GroupMemory -----------------------------
(***************************************************************************)
(* The memory of a group by whose lanes spill their own tables              *)
(* (docs/design/16-queries.md, "The lanes' spill"): the budget the lanes   *)
(* reserve their tables' growth in, the spill of a lane the budget refuses, *)
(* and the merge of a retiring lane's table into the one the retired lanes *)
(* share. The code is AggregationPartition.ReserveAhead and GiveBackAhead, *)
(* ArrayShelf, and the lanes' merge in AggregationEngine.                   *)
(*                                                                          *)
(* Memory is counted in units. A lane folds its batches, a batch growing   *)
(* its table by a unit or not, after reserving the unit; refused, it      *)
(* writes its table to the scratch and carries on with it emptied. A lane  *)
(* out of rows retires: once the budget shows room for what the merge     *)
(* grows the shared table by, it merges its table into it, the doubling   *)
(* first, then the copy, then its own table given back; without room, it  *)
(* writes its table to the scratch. A doubling the budget refuses still   *)
(* happens, since a merge cannot stop halfway: that is the overshoot.     *)
(*                                                                          *)
(* ReserveAhead is the fix of 1a8d2ac7: the room is reserved when the     *)
(* merge is decided, rather than only seen, so the lanes that keep folding *)
(* meanwhile cannot take it.                                                *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Lanes,          \* the lanes of the group by
    Ceiling,        \* the query's budget, in units
    Batches,        \* the batches each lane folds
    ReserveAhead    \* switch: a merge reserves its growth when it is decided (1a8d2ac7)

VARIABLES
    table,      \* [Lanes -> the units its table holds]
    left,       \* [Lanes -> the batches it has still to fold]
    pc,         \* [Lanes -> "fold", "merge", "copy" or "done"]
    shared,     \* the units the retired lanes' shared table holds
    credit,     \* the units reserved ahead for the merge in progress and not yet used
    reserved,   \* the units the budget counts: tables, the shared table, and the credit
    spills,     \* history: the runs the lanes wrote to the scratch
    merged,     \* history: the lanes whose tables went into the shared one
    peak        \* history: the most units held at once

vars == <<table, left, pc, shared, credit, reserved, spills, merged, peak>>

RECURSIVE SumOver(_, _)
SumOver(t, S) == IF S = {} THEN 0 ELSE LET l == CHOOSE x \in S : TRUE IN t[l] + SumOver(t, S \ {l})

\* What the lanes' tables and the shared table hold.
Held(t, s) == SumOver(t, Lanes) + s

Init == /\ table = [l \in Lanes |-> 0]
        /\ left = [l \in Lanes |-> Batches]
        /\ pc = [l \in Lanes |-> "fold"]
        /\ shared = 0
        /\ credit = 0
        /\ reserved = 0
        /\ spills = 0
        /\ merged = {}
        /\ peak = 0

Room(n) == reserved + n <= Ceiling

\* A batch that grows the lane's table by a unit, its growth reserved first; refused, the table goes to
\* the scratch as a run and the lane carries on with it emptied.
Grow(l) ==
    /\ pc[l] = "fold" /\ left[l] > 0
    /\ IF Room(1)
       THEN /\ table' = [table EXCEPT ![l] = @ + 1]
            /\ reserved' = reserved + 1
            /\ UNCHANGED spills
       ELSE /\ table' = [table EXCEPT ![l] = 0]
            /\ reserved' = reserved - table[l]
            /\ spills' = spills + 1
    /\ left' = [left EXCEPT ![l] = @ - 1]
    /\ peak' = IF Held(table', shared) > peak THEN Held(table', shared) ELSE peak
    /\ UNCHANGED <<pc, shared, credit, merged>>

\* A batch whose groups the table holds already.
Fold(l) ==
    /\ pc[l] = "fold" /\ left[l] > 0
    /\ left' = [left EXCEPT ![l] = @ - 1]
    /\ UNCHANGED <<table, pc, shared, credit, reserved, spills, merged, peak>>

\* Out of rows: the merge decided when the budget shows room for the shared table's doubling, by as many
\* units as the lane's table holds, reserved as its credit when ReserveAhead, only seen otherwise. A merge
\* in progress, or no room, and the lane writes its table to the scratch instead.
Retire(l) ==
    /\ pc[l] = "fold" /\ left[l] = 0
    /\ IF table[l] > 0 /\ ~(\E m \in Lanes : pc[m] \in {"merge", "copy"}) /\ Room(table[l])
       THEN /\ pc' = [pc EXCEPT ![l] = "merge"]
            /\ credit' = IF ReserveAhead THEN table[l] ELSE 0
            /\ reserved' = IF ReserveAhead THEN reserved + table[l] ELSE reserved
            /\ UNCHANGED <<table, spills>>
       ELSE /\ pc' = [pc EXCEPT ![l] = "done"]
            /\ table' = [table EXCEPT ![l] = 0]
            /\ reserved' = reserved - table[l]
            /\ spills' = spills + IF table[l] > 0 THEN 1 ELSE 0
            /\ UNCHANGED credit
    /\ UNCHANGED <<left, shared, merged, peak>>

\* The doubling: out of the credit, or reserved now and taken past the budget when it refuses, since the
\* merge cannot stop halfway.
Double(l) ==
    /\ pc[l] = "merge"
    /\ shared' = shared + table[l]
    /\ reserved' = IF ReserveAhead THEN reserved ELSE reserved + table[l]
    /\ credit' = 0
    /\ pc' = [pc EXCEPT ![l] = "copy"]
    /\ peak' = IF Held(table, shared') > peak THEN Held(table, shared') ELSE peak
    /\ UNCHANGED <<table, left, spills, merged>>

\* The copy into the shared table's new arrays, then the lane's table given back.
Copy(l) ==
    /\ pc[l] = "copy"
    /\ table' = [table EXCEPT ![l] = 0]
    /\ reserved' = reserved - table[l]
    /\ merged' = merged \cup {l}
    /\ pc' = [pc EXCEPT ![l] = "done"]
    /\ UNCHANGED <<left, shared, credit, spills, peak>>

Next == \E l \in Lanes : Grow(l) \/ Fold(l) \/ Retire(l) \/ Double(l) \/ Copy(l)

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

(* ------------------------------ the properties ------------------------------ *)

\* What the lanes' tables and the shared table hold never passes the budget.
WithinCeiling == Held(table, shared) <= Ceiling

\* The budget counts at least what is held.
Counted == Held(table, shared) <= reserved

\* Every lane ends: no wait of the protocol blocks for ever.
EveryLaneEnds == <>(\A l \in Lanes : pc[l] = "done")

\* Witness: a lane's table goes into the shared one while another lane still folds.
NoMergeBesideFolds == ~(\E l, m \in Lanes : l # m /\ pc[l] \in {"merge", "copy"} /\ pc[m] = "fold" /\ left[m] > 0)

\* Witness: a lane writes its table to the scratch.
NoSpill == spills = 0
================================================================================
