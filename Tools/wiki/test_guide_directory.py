import unittest
from guide_directory import directory, section_for


class DirectoryTests(unittest.TestCase):
    def test_imported_pages_are_grouped_by_gameplay_subject(self):
        for path, expected in [('imported/shitmed/medical/surgery', 'medical'), ('imported/nf/piloting', 'piloting'), ('imported/mono/robotics/mechs', 'science'), ('imported/nf/mobs/diona', 'species'), ('imported/nf/cargo-hauling', 'cargo')]:
            self.assertEqual(section_for('guidebook/in_game/' + path), expected)

    def test_only_existing_unique_nonpolicy_links(self):
        pages = [{'path':'guidebook/in_game/medical/chemist', 'title':'Химик'}, {'path':'guidebook/in_game/imported/mono/factions/tsfmc-sop', 'title':'SOP'}]
        content = directory(pages + pages)
        self.assertEqual(content.count('/ru/guidebook/in_game/medical/chemist'), 1)
        self.assertNotIn('SOP', content)
        self.assertIn('guides-medical', content)


if __name__ == '__main__':
    unittest.main()
