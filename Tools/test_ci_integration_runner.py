import argparse
import unittest
from unittest.mock import patch

import run_integration_tests as runner


class IntegrationRunnerTests(unittest.TestCase):
    def test_filters_partition_every_fixture_and_keep_the_shipyard_exclusion(self):
        examples = {
            'prototype-client': 'Content.IntegrationTests.Tests.PrototypeTests.PrototypeTests.ClientPrototypeSaveLoadSaveTest',
            'prototype-server': 'Content.IntegrationTests.Tests.PrototypeTests.PrototypeTests.TestAllServerPrototypesAreSerializable',
            'prototype-spawn': 'Content.IntegrationTests.Tests.PrototypeSaveTest.UninitializedSaveTest',
            'maps': 'Content.IntegrationTests.Tests.PostMapInitTest.GameMapsLoadableTest',
            'entities': 'Content.IntegrationTests.Tests.EntityTest.SpawnAndDeleteAllEntitiesInTheSameSpot',
            'fork-content': 'Content.IntegrationTests.Tests._DeepLagoon.CharacterEditorTests.Example',
            'interactions': 'Content.IntegrationTests.Tests.Chemistry.Example',
            'remaining': 'Content.IntegrationTests.Tests.NewFixture.NewTest',
        }
        for expected, name in examples.items():
            self.assertEqual(expected, runner.group_for(name))
        for expression in runner.filters().values():
            self.assertTrue(expression.startswith(runner.BASE_FILTER))
        remaining = runner.filters()['remaining']
        for terms in runner.GROUPS.values():
            for term in terms:
                self.assertIn('&FullyQualifiedName!~' + term, remaining)

    def test_fixture_parser_ignores_parameter_dots_quotes_and_generic_types(self):
        examples = {
            'Content.IntegrationTests.Tests.Sample.Test(0.5,"a.b)]")': 'Content.IntegrationTests.Tests.Sample.',
            'Content.IntegrationTests.Tests.Sample("a.b").Test(1)': 'Content.IntegrationTests.Tests.Sample',
            'Content.IntegrationTests.Tests.Sample[System.String].Test(1)': 'Content.IntegrationTests.Tests.Sample',
        }
        for name, expected in examples.items():
            self.assertEqual(expected, runner.fixture_prefix(name))

    def test_discovery_merges_overlaps_and_keeps_every_new_fixture(self):
        names = [
            'Content.IntegrationTests.Tests.Sample[System.String].One(0.5)',
            'Content.IntegrationTests.Tests.SampleExtra.Two()',
            'Content.IntegrationTests.Tests.FutureFixture.NewCase()',
            'Content.IntegrationTests.Tests._DeepLagoon.NewFeature.Example()',
        ]
        discovered = runner.discover_fixtures('\n'.join('    ' + name for name in names))
        for name in names:
            selections = discovered[runner.group_for(name)].values()
            self.assertEqual(1, sum(any(prefix in name for prefix in prefixes) for prefixes in selections))
        self.assertEqual(2, len(discovered['remaining']))
        self.assertEqual(1, len(discovered['fork-content']))
        with self.assertRaises(RuntimeError):
            runner.discover_fixtures('No tests were discovered')

    def test_fork_methods_release_worlds_between_methods_and_keep_all_cases(self):
        names = [
            'Content.IntegrationTests.Tests._DeepLagoon.Example.First(True)',
            'Content.IntegrationTests.Tests._DeepLagoon.Example.First(False)',
            'Content.IntegrationTests.Tests._DeepLagoon.Example.Second()',
        ]
        selected = runner.discover_fixtures("\n".join(names))["fork-content"]
        self.assertEqual(2, len(selected))
        for name in names:
            self.assertEqual(1, sum(any(prefix in name for prefix in prefixes) for prefixes in selected.values()))

    def test_failed_group_fails_the_run_without_skipping_later_groups(self):
        batches = [(name, expression, name) for name, expression in runner.filters().items()]
        with patch('builtins.print'), patch('sys.argv', ['runner']), patch.object(runner.Path, 'mkdir'), \
                patch.object(runner, 'test_batches', return_value=batches), \
                patch.object(runner.subprocess, 'run') as run:
            run.side_effect = [argparse.Namespace(returncode=int(i == 1)) for i in range(len(batches))]
            self.assertEqual(1, runner.main())
            self.assertEqual(len(batches), run.call_count)
            for call in run.call_args_list:
                self.assertIn('NUnit.MapWarningTo=Failed', call.args[0])
                self.assertEqual('0', call.kwargs['env']['DOTNET_gcServer'])
                self.assertEqual('0x200000000', call.kwargs['env']['DOTNET_GCHeapHardLimit'])

    def test_shards_are_disjoint_and_cover_all_batches(self):
        batches = [(str(index), 'filter-' + str(index), str(index)) for index in range(13)]
        selected = []
        for shard in range(4):
            with patch('builtins.print'), patch('sys.argv', ['runner', '--shard-index', str(shard), '--shard-count', '4']), \
                    patch.object(runner.Path, 'mkdir'), patch.object(runner, 'test_batches', return_value=batches), \
                    patch.object(runner.subprocess, 'run', return_value=argparse.Namespace(returncode=0)) as run:
                self.assertEqual(0, runner.main())
                selected.extend(call.args[0][call.args[0].index('--filter') + 1] for call in run.call_args_list)
        self.assertEqual(sorted(expression for _, expression, _ in batches), sorted(selected))


if __name__ == '__main__':
    unittest.main()
