"""Summarize three sequential runs of each probe. Raw CSV files may be in a directory or a zip."""
import csv
import io
import statistics
import sys
import zipfile
from collections import defaultdict
from contextlib import nullcontext
from pathlib import Path


def read_rows(source, archive, name):
    if archive is None:
        with (source / name).open(encoding="utf-8-sig", newline="") as f:
            return list(csv.DictReader(f))
    with archive.open(name) as raw, io.TextIOWrapper(raw, encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def write_summary(source, archive, output, suite, keys, measurement, slots):
    groups = defaultdict(lambda: defaultdict(list))
    raw_count = 0
    case_sets = []
    for rep in range(3):
        run = read_rows(source, archive, f"{suite}-{rep}.csv")
        run_keys = set()
        for row in run:
            assert int(row["rep"]) == rep
            key = tuple(row[k] for k in keys)
            run_keys.add(key)
            groups[key][rep].append(row)
        raw_count += len(run)
        case_sets.append(run_keys)
    assert case_sets[0] == case_sets[1] == case_sets[2]
    bytes_name = "bytes_per_scan" if suite == "corpus" else "bytes_per_call"
    block_key = "batch" if suite == "corpus" else "block"
    with output.open("w", encoding="utf-8", newline="") as dest:
        writer = csv.writer(dest, lineterminator="\n")
        writer.writerow([*keys, measurement, "rep0", "rep1", "rep2", bytes_name])
        for key in sorted(groups, key=lambda k: (int(k[0]), *k[1:])):
            runs = groups[key]
            medians, allocations = [], []
            for rep in range(3):
                batch = runs[rep]
                assert sorted(int(r[block_key]) for r in batch) == list(range(slots))
                if suite != "corpus":
                    assert sorted(int(r["slot"]) for r in batch) == list(range(slots))
                medians.append(statistics.median(float(r[measurement]) for r in batch))
                allocations.append(statistics.median(float(r[bytes_name]) for r in batch))
            writer.writerow([*key, round(statistics.median(medians), 4), *[round(n,4) for n in medians],
                             round(statistics.median(allocations), 2)])
    return raw_count, len(groups)


def main():
    source, output = map(Path, sys.argv[1:])
    output.mkdir(exist_ok=True)
    with (zipfile.ZipFile(source) if source.is_file() else nullcontext()) as archive:
        matrix_keys = ["length", "shape", "pieces", "case", "needle_units", "start", "expected",
                       "route", "runtime", "dll_sha256"]
        corpus_keys = ["fragmented_percent", "task", "route", "checksum", "runtime", "dll_sha256"]
        for suite, keys, unit, slots in [("matrix", matrix_keys, "ns_per_call", 3),
                                         ("short", matrix_keys, "ns_per_call", 3),
                                         ("corpus", corpus_keys, "ms_per_scan", 4)]:
            raw, grouped = write_summary(source, archive, output / f"{suite}.csv", suite, keys, unit, slots)
            print(f"{suite}: {raw} batches, {grouped} summary rows")


if __name__ == "__main__":
    main()
