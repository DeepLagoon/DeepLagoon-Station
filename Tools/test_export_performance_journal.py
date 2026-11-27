import json
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest

from export_performance_journal import records, timestamp


class PerformanceJournalExportTests(unittest.TestCase):
    def test_export_keeps_actual_points_and_original_report_lines_in_time_order(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "round-000213.db"
            with closing(sqlite3.connect(path)) as db, db:
                db.executescript("CREATE TABLE samples(id INTEGER PRIMARY KEY,utc REAL,run TEXT,payload TEXT);"
                                 "CREATE TABLE reports(id INTEGER PRIMARY KEY,utc REAL,run TEXT,kind TEXT,line TEXT);")
                db.execute("INSERT INTO samples VALUES(1,100,'run-1',?)", (json.dumps(dict(TickMs=12.5)),))
                db.execute("INSERT INTO samples VALUES(2,300,'run-1',?)", (json.dumps(dict(TickMs=80)),))
                db.execute("INSERT INTO reports VALUES(1,200,'run-1','tick','Slow server tick: tick=77 elapsed_ms=80')")
            exported = list(records(path))
            self.assertEqual([row["utcSeconds"] for row in exported], [100, 200, 300])
            self.assertEqual(exported[1]["line"], "Slow server tick: tick=77 elapsed_ms=80")
            self.assertEqual(exported[2]["sample"]["TickMs"], 80)
            self.assertEqual(len(list(records(path, 200, 200))), 1)
            self.assertEqual(len(list(records(path, kind="reports"))), 1)

    def test_missing_journal_is_not_created_and_dates_are_utc(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "does-not-exist.db"
            with self.assertRaises(sqlite3.OperationalError):
                list(records(path))
            self.assertFalse(path.exists())
        self.assertEqual(timestamp("1970-01-01T00:01:40Z"), 100)
        self.assertEqual(timestamp("1970-01-01T03:01:40+03:00"), 100)


if __name__ == "__main__":
    unittest.main()
