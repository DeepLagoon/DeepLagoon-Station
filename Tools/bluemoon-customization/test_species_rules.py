import unittest

from species_rules import classify


class SpeciesRulesTests(unittest.TestCase):
    def marking(self, identifier, category='HeadSide', **fields):
        return classify({'id': identifier, 'markingCategory': category, **fields})

    def test_approved_families(self):
        for identifier, expected in [
            ('BlueMoonTailsHumanFoxTail', ['Vulpkanin']),
            ('BlueMoonTailsHumanCatTail', ['Tajaran', 'Ariral']),
            ('BlueMoonSnoutsMam_snoutsMouseSnout', ['Rodentia']),
            ('BlueMoonMam_body_markingsSharkHead', ['Feroxi']),
            ('BlueMoonSnoutsMam_snoutsBirdSnout', ['Harpy', 'Resomi', 'Vox']),
            ('BlueMoonSnoutsGatorSnout', ['Reptilian', 'Hydrakin']),
            ('BlueMoonDeco_wingsMonarchChest', ['Moth']),
            ('BlueMoonSnoutsMam_snoutsScarabSnout', ['Chitinid']),
            ('BlueMoonSnoutsMam_snoutsTarantulaSnout', ['Arachnid']),
            ('BlueMoonHairDionaVineHeadTop', ['Diona']),
            ('BlueMoonWingsRoboticChest', ['Synth', 'IPC']),
        ]:
            with self.subTest(identifier=identifier):
                self.assertEqual(self.marking(identifier), expected)

    def test_unrepresented_animals_and_fantasy_wings_require_trait(self):
        for identifier in ['BlueMoonEarsHumanBearHeadSide', 'BlueMoonEarsBunnyHeadSide',
                           'BlueMoonHornsAntlerHeadTop', 'BlueMoonTailsHumanCowTail',
                           'BlueMoonTailsMam_tailsXenoTail', 'BlueMoonDeco_wingsDragonChest',
                           'BlueMoonDeco_wingsAngelChest']:
            with self.subTest(identifier=identifier):
                self.assertEqual(self.marking(identifier, 'Wings' if 'wings' in identifier else 'HeadSide'), [])

    def test_shared_scar_does_not_match_scarab_anatomy(self):
        self.assertEqual(self.marking('BlueMoonMam_body_markingsFace_scar_lHead'), 'common')
        self.assertEqual(self.marking('BlueMoonMam_body_markingsTattooHeartHead'), 'common')
        self.assertEqual(self.marking('BlueMoonMam_body_markingsPlain_styleTorsoChest'), 'common')
        self.assertEqual(self.marking('BlueMoonSnoutsMam_snoutsScarabSnout'), ['Chitinid'])

    def test_native_assignments_and_hair_are_unchanged(self):
        self.assertIsNone(self.marking('ReptilianFrills', speciesRestriction=['Reptilian']))
        self.assertIsNone(self.marking('BlueMoonHairNovaHair', 'Hair'))


if __name__ == '__main__':
    unittest.main()
