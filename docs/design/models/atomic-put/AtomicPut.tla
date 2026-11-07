------------------------------ MODULE AtomicPut ------------------------------
(***************************************************************************)
(* The store contract a dataset relies on ("The store abstraction" in      *)
(* docs/design/13-dataset.md): a key holds nothing or one whole object, a  *)
(* creation succeeds only on a free key, and an object leaves a key only   *)
(* through a removal that names it. Each object is named by the put that   *)
(* wrote it, which is what a put's answer is about.                         *)
(*                                                                          *)
(* This module owns the contract. ConditionalPut and FilePut are checked   *)
(* to refine it, and the dataset's models take the store through it.       *)
(***************************************************************************)
CONSTANTS
    Keys,       \* the keys of the store
    Puts,       \* the puts that may write an object, each naming the object it writes
    None,       \* what a free key holds
    Removable   \* whether an object may leave its key, as vacuum makes it

ASSUME None \notin Puts
ASSUME Removable \in BOOLEAN

VARIABLE slots      \* [Keys -> Puts \cup {None}]: whose object each key holds

TypeOK == slots \in [Keys -> Puts \cup {None}]

Init == slots = [k \in Keys |-> None]

\* A creation: on a free key only, and whole at once.
Create(k, p) == /\ slots[k] = None
                /\ slots' = [slots EXCEPT ![k] = p]

\* A removal that names the object a key holds.
Remove(k) == /\ Removable
             /\ slots[k] # None
             /\ slots' = [slots EXCEPT ![k] = None]

Next == \E k \in Keys : (\E p \in Puts : Create(k, p)) \/ Remove(k)

SafetySpec == Init /\ [][Next]_slots

\* An object is never replaced by another in one step: a key goes back to free first.
NeverReplaced == [][\A k \in Keys : slots[k] # None => slots'[k] \in {slots[k], None}]_slots
=============================================================================
