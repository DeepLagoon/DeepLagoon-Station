from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import audit_book_links
from game_data import GameData


class BookLinkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.station = Path(self.temp.name)
        self.data = GameData(self.station)
        self.data.raw['entity'].update({
            'BaseGuidebook': {'abstract':True, 'components':[{'type':'Tag', 'tags':['Book']}, {'type':'GuideHelp', 'openOnActivation':True, 'guides':['NF14']}]},
            'BookSecurity': {'parent':'BaseGuidebook', 'name':'Безопасность', 'components':[{'type':'GuideHelp', 'guides':['Security']}]},
            'OrdinaryNovel': {'components':[{'type':'Tag', 'tags':['Book']}, {'type':'Paper', 'content':'story'}]},
        })
        source = self.station / 'Resources/ServerInfo/Guidebook/Security/Security.xml'
        source.parent.mkdir(parents=True)
        source.write_text('<Document>Security</Document>', encoding='utf-8')
        self.data.raw['guideEntry']['Security'] = {'text':'/ServerInfo/Guidebook/Security/Security.xml'}
        self.target = '/ru/guidebook/in_game/books/security'
        self.pages = {
            self.target.removeprefix('/ru/'): {'content':'<div class="prose"><section class="dl-chapter"><h2>Глава 1</h2><p>Инструкция службы безопасности.</p></section></div>'},
            'guidebook/in_game/security/security': {'content':'<p>Инструкция службы безопасности.</p>'},
        }

    def check(self, defaults=None, routes=None):
        with patch.dict(audit_book_links.BOOK_TOPICS, {'BookSecurity':['Security']}, clear=True), patch.dict(audit_book_links.BOOK_SOURCE_PATHS, {}, clear=True):
            return audit_book_links.inspect(self.data, defaults or {'BookSecurity':self.target}, routes or {'entity:BookSecurity':self.target}, self.pages)

    def test_inherited_activation_and_book_tag_are_checked_without_claiming_paper_novels(self):
        report = self.check()
        self.assertEqual(report['books'], 1)
        self.assertEqual(report['issues'], [])

    def test_generic_inherited_topic_is_rejected(self):
        self.data.raw['entity']['BookSecurity']['components'] = []
        self.assertTrue(any(issue.get('expectedTopics') == ['Security'] for issue in self.check()['issues']))

    def test_existing_page_with_unrelated_contents_is_rejected(self):
        self.pages[self.target.removeprefix('/ru/')]['content'] = '<div class="prose"><section class="dl-chapter">Обзор фракции.</section></div>'
        self.assertTrue(any(issue.get('missingTopicContent') == 'Security' for issue in self.check()['issues']))

    def test_default_and_configured_targets_must_both_match(self):
        self.assertTrue(any('default' in issue for issue in self.check(defaults={'BookSecurity':'/ru/in_game'})['issues']))
        self.assertTrue(any('configured' in issue for issue in self.check(routes={'entity:BookSecurity':'/ru/in_game'})['issues']))

    def test_new_inherited_guidebook_requires_review(self):
        self.data.raw['entity']['NewManual'] = {'parent':'BaseGuidebook'}
        self.assertTrue(any('NewManual' in issue.get('unreviewedBooks', []) for issue in self.check()['issues']))

    def test_reviewed_wiki_source_does_not_require_reactivating_disabled_game_guide(self):
        self.data.raw['entity']['BookSecurity']['components'] = []
        del self.data.raw['guideEntry']['Security']
        with patch.dict(audit_book_links.BOOK_TOPICS, {'BookSecurity':['Security']}, clear=True):
            report = audit_book_links.inspect(self.data, {'BookSecurity':self.target}, {'entity:BookSecurity':self.target}, self.pages)
        self.assertEqual(report['issues'], [])


if __name__ == '__main__':
    unittest.main()
