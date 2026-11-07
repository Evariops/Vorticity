# Models

The protocols of Vorticity that run across processes and over a store, where a wrong interleaving
loses or doubles rows without a word, are written here as TLA+ specifications and model-checked with
TLC. A test draws some interleavings. TLC walks every state a small configuration can reach, and
answers with a counterexample, a numbered trace from the start to the violation, when a property
fails.

A model proves facts about the model, not about the C#. Each one therefore names the code it mirrors,
at a commit, action by action, and each property it claims for the code names the test that holds the
code to it. The design documents stay the readable contract and link their models as evidence.

| model | what it checks |
|---|---|
| [atomic-put](atomic-put/README.md) | the store contract the dataset relies on: a key holds nothing or one whole object, created once |
| [conditional-put](conditional-put/README.md) | a put-if-absent over HTTP whose answers are lost, settled by the put's id and a head |
| [file-put](file-put/README.md) | `FileObjectStore`'s creation of a key, the reads that meet it, and the repair of a torn commit |
| [dataset-commit](dataset-commit/README.md) | the commit by put-if-absent and the rebase of appends, deletes and compactions onto the version that won |
| [dataset-sources](dataset-sources/README.md) | the offsets that count each entry of a source once, through stale handles and replays after a lost answer |
| [dataset-lifecycle](dataset-lifecycle/README.md) | the trust span, the commit's guard on old objects and vacuum, against clocks that drift and a listing that lags |

## Three kinds of configurations

Every model has configurations of three kinds, each a `.cfg` file beside its module.

- The positive one, which must pass: TLC finds no violation in the whole state space it describes.
- `Broken*.cfg`, each a mutation that must be caught. It removes a guard, puts back a defect that was
  fixed, or models a fix that does not work, and it must violate the one property it names. A
  mutation that passes means the property no longer bites.
- `Reachability*.cfg`, each a race the model must reach. Its invariant says the race never happens,
  and must be violated by a trace that runs the race in the order it names. Without them, a model that
  never reaches the interleaving it is about would pass and prove nothing.

[expected-exit-codes.txt](expected-exit-codes.txt) records the exit code TLC must give for each
configuration: 0 when nothing is violated, 12 for an invariant violated, 13 for a temporal property.

## Running them

```sh
JAVA=/path/to/java tools/models.sh                       # every model
JAVA=/path/to/java tools/models.sh docs/design/models/file-put
```

The script downloads the pinned `tla2tools.jar` (1.7.4, the last stable release, from the TLA+
project's own releases) into the user's cache, checks its SHA-256 on every run, and runs each
configuration with `-workers 1 -seed 1 -fp 1`, so that a run is the same run everywhere. The
configurations run side by side, as many at once as the machine has processors unless `MODELS_JOBS`
says otherwise, each with a temporary directory of its own, where TLC extracts the standard modules it
parses. The `Deep*.cfg` configurations, which take minutes each, run only with `MODELS_DEEP=1`. It needs
Java 11 or later. On macOS, `brew install openjdk@21` installs one, which the script can be given by its path
without linking it into the system. A configuration whose code differs from the manifest fails the
run, and so does a timeout of one the manifest lists. CI runs every model when one of them, the
manifest or the script changes.

A counterexample is printed by TLC as states, each preceded by the action that led to it, with the
line of the module that defines that action. Each model's README walks through the traces its broken
configurations give.

## Adding one

A model gets a directory of its own here, a module whose name is unique in the repository, its
configurations, and a README with the same parts as the others: what it covers and what it does not
claim, its properties, its configurations, how to run it, and the evidence TLC gave, with the tool,
the Java, the states, the depth, and the trace of each broken and reachability configuration. Each
action names in a comment the file and lines of the code it models. A model that consumes another's
contract takes its module by `INSTANCE` rather than copying it, rechecks its properties under its own
actions, and keeps the identities the code joins on, such as a version number or a put's id.
