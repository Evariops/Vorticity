---------------------------- MODULE DatasetRollups ----------------------------
(***************************************************************************)
(* The rollups of a dataset against the commits that change their objects  *)
(* (docs/design/15-compaction.md, "Rollups"; DatasetCommitter.ApplyAsync,  *)
(* PutRollup and InvalidateAsync).                                          *)
(*                                                                          *)
(* One level of objects, each under a tree key, with the rows it counts.   *)
(* A rollup covers a range of tree keys and holds the total of its objects, *)
(* and a fingerprint of what it summed. Writers put an object under a free *)
(* key, take one away, or mark rows in one, which puts its entry again     *)
(* with fewer rows. A builder reads the latest version, sums a range, and  *)
(* commits the rollup later, once other commits may have landed. Every    *)
(* commit applies to the latest version, as a rebase does.                 *)
(*                                                                          *)
(* Two rules keep a rollup true, each a switch a mutation turns off:       *)
(* CheckFingerprint, a rollup put only over what its range holds as it     *)
(* was summed; TakeAway, a commit that puts or removes an entry taking    *)
(* away every rollup whose range holds its key.                            *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets, Sequences

CONSTANTS
    Keys,               \* the tree keys of the level, a few integers
    Writers,            \* each changes the object under its own key, Changes times
    Changes,
    Builders,           \* each sums and commits Rollups rollups
    Rollups,
    CheckFingerprint,   \* switch: a rollup put only over what its range holds as it was summed
    TakeAway            \* switch: a commit that changes an entry takes away the rollups over its key

ASSUME Keys \subseteq Nat

\* An entry: the object's uid and the rows it counts; None when no object is under the key.
None == [uid |-> 0, rows |-> 0]

\* A builder with no rollup summed.
NoPlan == [lo |-> 0, hi |-> 0, total |-> 0, fp |-> {}]

VARIABLES
    history,    \* the versions: [entries |-> [Keys -> entry], rollups |-> set of rollups]
    nextUid,    \* the next fresh uid
    wleft,      \* [Writers -> changes left]
    plan,       \* [Builders -> the rollup summed and not committed yet, or NoPlan]
    bleft,      \* [Builders -> rollups left]
    dropped     \* history: rollups a commit dropped as stale

vars == <<history, nextUid, wleft, plan, bleft, dropped>>

Latest == history[Len(history)]
Covered(entries, lo, hi) == {k \in Keys : lo <= k /\ k <= hi /\ entries[k] # None}

RECURSIVE SumOver(_, _)
SumOver(entries, S) == IF S = {} THEN 0 ELSE LET k == CHOOSE x \in S : TRUE IN entries[k].rows + SumOver(entries, S \ {k})

\* What a builder hashes: every entry of the range, its key, uid and rows.
Fingerprint(entries, lo, hi) == {<<k, entries[k].uid, entries[k].rows>> : k \in Covered(entries, lo, hi)}

\* The writers' keys are apart: writer i changes key i.
KeyOf(w) == CHOOSE k \in Keys : k = w

InitEntries == [k \in Keys |-> [uid |-> k, rows |-> 2]]

Init == /\ history = <<[entries |-> InitEntries, rollups |-> {}]>>
        /\ nextUid = Cardinality(Keys) + 1
        /\ wleft = [w \in Writers |-> Changes]
        /\ plan = [b \in Builders |-> NoPlan]
        /\ bleft = [b \in Builders |-> Rollups]
        /\ dropped = 0

\* A commit of a change under key k: the entry set, and the rollups over k taken away.
Commit(entries) ==
    LET k == CHOOSE x \in Keys : entries[x] # Latest.entries[x] IN
    history' = Append(history, [entries |-> entries,
                                rollups |-> IF TakeAway THEN {r \in Latest.rollups : ~(r.lo <= k /\ k <= r.hi)}
                                            ELSE Latest.rollups])

\* A writer puts a fresh object under its key, takes the one there away, or marks a row in it.
Change(w) ==
    /\ wleft[w] > 0
    /\ LET k == KeyOf(w)
           e == Latest.entries[k]
       IN \/ /\ e = None
             /\ Commit([Latest.entries EXCEPT ![k] = [uid |-> nextUid, rows |-> 2]])
             /\ nextUid' = nextUid + 1
          \/ /\ e # None
             /\ Commit([Latest.entries EXCEPT ![k] = None])
             /\ UNCHANGED nextUid
          \/ /\ e # None /\ e.rows > 1
             /\ Commit([Latest.entries EXCEPT ![k] = [e EXCEPT !.rows = @ - 1]])
             /\ UNCHANGED nextUid
    /\ wleft' = [wleft EXCEPT ![w] = @ - 1]
    /\ UNCHANGED <<plan, bleft, dropped>>

\* A builder sums a range of the latest version (DatasetRollups.RollUpAsync).
Sum(b) ==
    /\ plan[b] = NoPlan /\ bleft[b] > 0
    /\ \E lo, hi \in Keys :
         /\ lo <= hi /\ Covered(Latest.entries, lo, hi) # {}
         /\ plan' = [plan EXCEPT ![b] = [lo |-> lo, hi |-> hi,
                        total |-> SumOver(Latest.entries, Covered(Latest.entries, lo, hi)),
                        fp |-> Fingerprint(Latest.entries, lo, hi)]]
    /\ bleft' = [bleft EXCEPT ![b] = @ - 1]
    /\ UNCHANGED <<history, nextUid, wleft, dropped>>

\* Its commit, on the latest version: put when the range holds what was summed, dropped otherwise.
PutRollup(b) ==
    /\ plan[b] # NoPlan
    /\ LET r == plan[b] IN
       IF CheckFingerprint /\ Fingerprint(Latest.entries, r.lo, r.hi) # r.fp
       THEN /\ dropped' = dropped + 1
            /\ UNCHANGED history
       ELSE /\ history' = Append(history, [entries |-> Latest.entries, rollups |-> Latest.rollups \cup {r}])
            /\ UNCHANGED dropped
    /\ plan' = [plan EXCEPT ![b] = NoPlan]
    /\ UNCHANGED <<nextUid, wleft, bleft>>

Next == \/ \E w \in Writers : Change(w)
        \/ \E b \in Builders : Sum(b) \/ PutRollup(b)

Spec == Init /\ [][Next]_vars

\* Every rollup of every version holds the total of the objects under its range, and what it summed.
RollupsTrue ==
    \A i \in 1..Len(history) : \A r \in history[i].rollups :
        /\ r.total = SumOver(history[i].entries, Covered(history[i].entries, r.lo, r.hi))
        /\ r.fp = Fingerprint(history[i].entries, r.lo, r.hi)

\* Witness: a rollup summed before a change is dropped at its commit.
NoDrop == dropped = 0

\* Witness: a change takes a rollup away.
NoTakenAway == \A i \in 2..Len(history) : history[i - 1].rollups \subseteq history[i].rollups
================================================================================
