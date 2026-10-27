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
            matching = [group for group, terms in runner.GROUPS.items() if any(term in name for term in terms)]
            self.assertEqual([expected], matching or ['remaining'])
        for test_filter in runner.filters().values():
            self.assertTrue(test_filter.startswith(runner.BASE_FILTER))
        remaining = runner.filters()['remaining']
        for terms in runner.GROUPS.values():
            for term in terms:
                self.assertIn('&FullyQualifiedName!~' + term, remaining)

    def test_failed_group_fails_the_run_without_skipping_later_groups(self):
        with patch('sys.argv', ['runner']), patch.object(runner.Path, 'mkdir'), \
                patch.object(runner.subprocess, 'run') as run:
            run.return_value.returncode = 0
            results = [type('Result', (), {'returncode': int(i == 1)})() for i in range(len(runner.filters()))]
            run.side_effect = results
            self.assertEqual(1, runner.main())
            self.assertEqual(len(runner.filters()), run.call_count)
            for call in run.call_args_list:
                self.assertIn('NUnit.MapWarningTo=Failed', call.args[0])
                self.assertEqual('0', call.kwargs['env']['DOTNET_gcServer'])


if __name__ == '__main__':
    unittest.main()
