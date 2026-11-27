# Meloberry Preset File Format Specification (`.mbypreset`)

> **Audience:** Developers maintaining **Meloberry PC (`mby-pc`)** and **`mby` CLI**, as well as AI/tooling authors reading or generating portable Meloberry study presets.

---

## 1. Overview & Purpose

A **`.mbypreset` file** is a portable UTF-8 JSON document capturing either:

1. A **whole workflow configuration** (`"scope": "workflow"`): target container (`session`, `standaloneArticle`, or `standaloneQuiz`), authoring format (`markdownDirectory`, `singleMarkdown`, or `json`), and an ordered array of Article/Quiz cards with their specs, subtypes, modifiers, notes, and instance ordering.
2. A **single component card** (`"scope": "component"`): one self-contained **Article** (`"kind": "article"`) or **Quiz** (`"kind": "quiz"`) card definition that can be saved from or loaded into an individual card inside **Decide the organization**.

---

## 2. File & Encoding Rules

- **Extension:** `.mbypreset`
- **Encoding:** UTF-8 (without BOM preferred)
- **JSON Naming:** `camelCase` property names (`System.Text.Json` `JsonNamingPolicy.CamelCase`)
- **Enum Serialization:** `camelCase` strings (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`)
- **Null Handling:** Optional null fields MAY be omitted on serialization (`JsonIgnoreCondition.WhenWritingNull`); consumers MUST deserialize case-insensitively and treat missing optional fields as `null`.
- **Write Semantics:** Always write atomically via a temporary sibling file (`<target>.tmp` → flush → atomic move/replace) so file watchers never read half-written JSON.

---

## 3. Envelope Schema (`MbyPresetFile`)

Every `.mbypreset` file MUST have `MbyPresetFile` as its root JSON object:

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `formatVersion` | `integer` | Yes | Schema version. Currently `1`. Consumers should reject unsupported major future versions gracefully or warn. |
| `scope` | `string` (`PresetFileScope`) | Yes | `"workflow"` or `"component"`. |
| `name` | `string` | Yes | Non-empty human-readable preset display name. |
| `description` | `string \| null` | No | Optional short summary describing the preset's pedagogical structure. |
| `workflowPreset` | `PromptPreset \| null` | Conditional | MUST be non-null when `scope == "workflow"`. |
| `componentPreset` | `PromptComponentPlan \| null` | Conditional | MUST be non-null when `scope == "component"`. |

---

## 4. Payload Reference

### 4.1 `PromptPreset` (Workflow Payload)

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `id` | `string` | Yes | Unique slug/ID (e.g., `"full-chapter-session"` or `"custom-a1b2c3d4e5"`). Generated automatically on import/save if empty. |
| `name` | `string` | Yes | Preset display name shown in the `Preset` dropdown. |
| `description` | `string` | No | Short description (defaults to `""`). |
| `container` | `ContainerKind` | Yes | `"session"`, `"standaloneArticle"`, or `"standaloneQuiz"`. |
| `authoringFormat` | `AuthoringFormat` | Yes | `"markdownDirectory"`, `"singleMarkdown"`, or `"json"`. |
| `selections` | `ShoppingListSelection[]` | Yes | Flattened summary list of spec selections (kept aligned with `components` for backward compatibility). |
| `isBuiltIn` | `boolean` | No | Always `false` for `.mbypreset` user/portable files. |
| `components` | `PromptComponentPlan[] \| null` | Recommended | Ordered list of component cards (`Article` and `Quiz`). |

### 4.2 `PromptComponentPlan` (Card Definition)

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `id` | `string` | Yes | Component identifier inside the workflow (e.g., `"a1"`, `"q1"`). Re-keyed or preserved safely when loaded into an existing card. |
| `kind` | `ComponentKind` | Yes | `"article"` (teaching blocks) or `"quiz"` (practice questions). When loading into an existing card, `kind` MUST match the target card (`article` ↔ `article`, `quiz` ↔ `quiz`). |
| `title` | `string` | Yes | Card heading (e.g., `"Core Concepts"`, `"Mastery Check"`). |
| `selections` | `ShoppingListSelection[]` | Yes | Spec topics, quantities, subtype/modifier keys, and topic instructions. |
| `orderedItems` | `OrderedSpecInstance[] \| null` | Optional | Explicit vertical order and per-instance subtypes inside the card. When present and non-empty, takes precedence over expanding `selections`. |

### 4.3 `ShoppingListSelection`

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `topic` | `string` | Yes | `mby spec` topic name (e.g., `"text"`, `"image"`, `"callout"`, `"table"`, `"link"`, `"mcq"`, `"flashcard"`, `"relabel"`, `"fill-blank"`, `"assertion-reason"`, `"spot-error"`, `"diff"`, `"mechanism"`). |
| `enabled` | `boolean` | Yes | `true` if included in this component. |
| `desiredCount` | `integer \| null` | No | Desired item count (`null` = agent/auto quantity). |
| `notes` | `string \| null` | No | Custom instructions for this topic within the component. |
| `modes` | `string[] \| null` | No | Active subtype and modifier keys (from `mby spec <topic>` mode groups). |
| `modeCounts` | `Record<string, integer> \| null` | No | Explicit primary-subtype counts (sum matches `desiredCount` when specified). |

### 4.4 `OrderedSpecInstance`

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `id` | `string` | Yes | Unique tile ID within the organizer UI (re-generated safely if missing). |
| `topic` | `string` | Yes | `mby spec` topic name. |
| `instanceNumber` | `integer` | Yes | 1-based ordinal of this topic inside the component (`1`, `2`, ...). |
| `subtype` | `string \| null` | No | Primary-group subtype key (e.g., `"pearl"`, `"clinical"`, `"single"`, `"diagram"`), or `null` for generic. |

---

## 5. Enums Reference

- **`PresetFileScope`**: `"workflow"` | `"component"`
- **`ContainerKind`**: `"session"` | `"standaloneArticle"` | `"standaloneQuiz"`
- **`ComponentKind`**: `"article"` | `"quiz"`
- **`AuthoringFormat`**: `"markdownDirectory"` | `"singleMarkdown"` | `"json"`

---

## 6. Storage & Portability Conventions

### Default Wrapper Library Locations
- **Whole-workflow preset library:** `%LOCALAPPDATA%\Meloberry\presets\*.mbypreset` (respecting `%MBY_WRAPPER_DATA%` override if set).
- **Suggested card preset directory:** `%LOCALAPPDATA%\Meloberry\presets\components\`.

### Arbitrary Save Paths & Registration Behavior
- Users may save a whole-workflow preset to **any directory on disk** (e.g., inside a study workspace, repository, or Desktop) via the centered Save dialog.
- When saved outside `%LOCALAPPDATA%\Meloberry\presets\`, Meloberry PC writes the portable `.mbypreset` file to the chosen path **and** mirrors a library copy under `%LOCALAPPDATA%\Meloberry\presets\` so it appears immediately in the `Preset` dropdown.
- Loading a workflow preset from anywhere registers/imports it cleanly into `%LOCALAPPDATA%\Meloberry\presets\` and applies it immediately.

---

## 7. Complete Examples

### 7.1 Workflow Scope (`"scope": "workflow"`)

```json
{
  "formatVersion": 1,
  "scope": "workflow",
  "name": "Neurophysiology Drill",
  "description": "Concise teaching overview paired with relabeling and MCQ drill.",
  "workflowPreset": {
    "id": "custom-91fa2c40e1",
    "name": "Neurophysiology Drill",
    "description": "Concise teaching overview paired with relabeling and MCQ drill.",
    "container": "session",
    "authoringFormat": "markdownDirectory",
    "isBuiltIn": false,
    "selections": [
      { "topic": "text", "enabled": true, "desiredCount": 2 },
      { "topic": "callout", "enabled": true, "desiredCount": 1, "modes": ["pearl"], "modeCounts": { "pearl": 1 } },
      { "topic": "mcq", "enabled": true, "desiredCount": 4, "modes": ["single"], "modeCounts": { "single": 4 } }
    ],
    "components": [
      {
        "id": "a1",
        "kind": "article",
        "title": "Action Potentials",
        "selections": [
          { "topic": "text", "enabled": true, "desiredCount": 2, "notes": "Include Nernst & GHK equations." },
          { "topic": "callout", "enabled": true, "desiredCount": 1, "modes": ["pearl"], "modeCounts": { "pearl": 1 } }
        ],
        "orderedItems": [
          { "id": "a1_text_1", "topic": "text", "instanceNumber": 1 },
          { "id": "a1_callout_1", "topic": "callout", "instanceNumber": 1, "subtype": "pearl" },
          { "id": "a1_text_2", "topic": "text", "instanceNumber": 2 }
        ]
      },
      {
        "id": "q1",
        "kind": "quiz",
        "title": "Rapid Check",
        "selections": [
          { "topic": "mcq", "enabled": true, "desiredCount": 4, "modes": ["single"], "modeCounts": { "single": 4 } }
        ]
      }
    ]
  }
}
```

### 7.2 Component Scope (`"scope": "component"`)

```json
{
  "formatVersion": 1,
  "scope": "component",
  "name": "High-Yield Quiz Card",
  "description": "Saved quiz component preset",
  "componentPreset": {
    "id": "q1",
    "kind": "quiz",
    "title": "High-Yield Quiz Card",
    "selections": [
      {
        "topic": "assertion-reason",
        "enabled": true,
        "desiredCount": 3,
        "notes": "Focus on causal pathophysiology.",
        "modes": ["4opt"],
        "modeCounts": { "4opt": 3 }
      },
      {
        "topic": "flashcard",
        "enabled": true,
        "desiredCount": 4,
        "modes": ["diagram"],
        "modeCounts": { "diagram": 4 }
      }
    ],
    "orderedItems": [
      { "id": "q1_ar_1", "topic": "assertion-reason", "instanceNumber": 1, "subtype": "4opt" },
      { "id": "q1_ar_2", "topic": "assertion-reason", "instanceNumber": 2, "subtype": "4opt" },
      { "id": "q1_ar_3", "topic": "assertion-reason", "instanceNumber": 3, "subtype": "4opt" },
      { "id": "q1_fc_1", "topic": "flashcard", "instanceNumber": 1, "subtype": "diagram" }
    ]
  }
}
```

---

## 8. Validation & Error Contract (and `mby` CLI Integration)

1. Reject missing/unreadable files or empty `name` with `Preset.InvalidFile`.
2. When importing a whole workflow preset, require `scope == "workflow"` and non-null `workflowPreset`.
3. When loading a card preset into an Organizer card, require `scope == "component"`, non-null `componentPreset`, and `componentPreset.kind == targetCardKind`.
4. **Future `mby` CLI commands** (such as `mby scaffold --preset <path.mbypreset>` or `mby apply`):
   - When `orderedItems` is present on a `PromptComponentPlan`, emit component blocks/questions in the exact `orderedItems` sequence before reconciling against `selections`.
   - When `orderedItems` is absent/null, expand `selections` deterministically using `desiredCount` and `modeCounts`.
