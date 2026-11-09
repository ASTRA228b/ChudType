"""Train ChudGPT Dictation v1 and export its small CPU checkpoint."""
import json
import math
import re
from pathlib import Path

import torch

TOKEN = re.compile(r"(?<![\w])(?:um+|uh+)(?![\w])", re.IGNORECASE)
MENTIONS = {"say", "said", "says", "saying", "word", "called", "named", "type",
            "typed", "spell", "spelled", "wrote", "write", "letter", "sound",
            "pronounce", "remove", "replace", "delete", "literal", "search", "for",
            "means", "stands", "is", "was", "and", "or", "versus", "with", "example"}
QUOTES = "\"'“”‘’"
SYMBOLS = "/\\_-+=#@<>\x60"

TRAIN_REMOVE = [
    "{f}, we should leave", "{f} do you have a minute", "so {f} I'll be there",
    "I was, {f}, hoping to join", "please {f}, check the numbers",
    "could you {f} send the files", "I wanted to {f} ask you something",
    "we can try, {f} another way", "{f} let's try again", "look, {f}, maybe later",
    "it's around {f} forty dollars", "tomorrow at {f} ten", "I have a {f} question",
    "please bring {f} three copies", "she walked {f} into the room",
    "the microphone is ready. {f}, let's go", "we'll meet {f} on Friday",
]
TRAIN_KEEP = [
    "{f}", "{f}.", "I said {f} yesterday", "type {f} in the input",
    "the word {f} is common", "{f} means micrometre", "search for {f} in history",
    "spelled {f} correctly", "replace {f} with another value", "an example is {f}",
    "the path is /{f}/lib", "the branch {f}-dev", "the name @{f} belongs to her",
    "the tag #{f} is required", "the literal {f} stands for something",
    "please use '{f}' in this sentence", 'then "{f} I think" was played',
    "it was ‘{f} maybe’ in the recording", "compare {f} versus er",
    "the sound {f} echoed", "we talked and {f}", "it was great or {f}.",
    "He wrote, {f}, in the chat", "they paused — {f}.",
]
VALIDATION = [
    ("Could you, um, check the calendar?", True),
    ("Uh, I'll look in the other folder.", True),
    ("We need um four of these.", True),
    ("I'm thinking, uh, about changing it.", True),
    ("The appointment starts at uh noon.", True),
    ("Please spell um for the transcript.", False),
    ('The clip says "um wait for me".', False),
    ("The file was /um/defaults.json.", False),
    ("I typed uh instead of OK.", False),
    ("The pronunciation hint says uh.", False),
    ("um!", False),
    ("The sample is “uh maybe”.", False),
    ("Please replace, um, with the full word.", False),
    ("Um stands for a unit in that file.", False),
]


def features(text, match):
    left = text[:match.start()].rstrip()
    right = text[match.end():].lstrip()
    head = re.search(r"(\w+)[,.!?;:]?$", left)
    tail = re.match(r"[,.:;!?]?\s*(\w+)", right)
    last, first = left[-1:] or "\0", right[:1] or "\0"
    return [
        float(bool(head)),
        float(bool(tail)),
        float(last == ","),
        float(first == ","),
        float(head is not None and head[1].lower() in MENTIONS),
        float(tail is not None and tail[1].lower() in MENTIONS),
        float(last in QUOTES or first in QUOTES or left.count('"') % 2 or
              left.count("“") > left.count("”") or left.count("‘") > left.count("’")),
        float(last in ".!?"),
        float(last in SYMBOLS or first in SYMBOLS),
    ]


def samples(templates, label):
    for template in templates:
        for filler in ("um", "uh", "umm", "uhh"):
            text = template.format(f=filler)
            yield features(text, TOKEN.search(text)), float(label)


def train():
    torch.manual_seed(288)
    torch.set_num_threads(1)
    rows = [*samples(TRAIN_REMOVE, True), *samples(TRAIN_KEEP, False)]
    x, y = map(torch.tensor, zip(*rows))
    net = torch.nn.Sequential(torch.nn.Linear(9, 12), torch.nn.Tanh(), torch.nn.Linear(12, 1))
    optimizer = torch.optim.AdamW(net.parameters(), lr=0.035, weight_decay=0.001)
    for _ in range(900):
        optimizer.zero_grad()
        loss = torch.nn.functional.binary_cross_entropy_with_logits(net(x).flatten(), y)
        loss.backward()
        optimizer.step()

    threshold = 0.97
    model = {
        "version": "ChudGPT Dictation v1",
        "threshold": threshold,
        "hiddenWeights": net[0].weight.tolist(),
        "hiddenBias": net[0].bias.tolist(),
        "outputWeights": net[2].weight[0].tolist(),
        "outputBias": net[2].bias.item(),
    }
    path = Path(__file__).resolve().parents[1] / "src/ChudType/Assets/dictation-v1.json"
    path.write_text(json.dumps(model, separators=(",", ":")) + "\n", encoding="utf-8")
    with torch.no_grad():
        predicted = [(text, torch.sigmoid(net(torch.tensor(features(text, TOKEN.search(text))))).item() >= threshold, want)
                     for text, want in VALIDATION]
    print(f"{sum(p.numel() for p in net.parameters())} parameters, {len(rows)} training examples")
    print(f"Held-out synthetic examples: {sum(got == want for _, got, want in predicted)}/{len(predicted)}")
    print(f"False removals: {sum(got and not want for _, got, want in predicted)}")
    for text, got, want in predicted:
        if got != want:
            print(f"  {'missed removal' if want else 'incorrect removal'}: {text}")


if __name__ == "__main__":
    train()
