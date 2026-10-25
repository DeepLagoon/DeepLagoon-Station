"""Reviewed wiki sources for books whose original guide entries are disabled.

Entity routes select these wiki editions without re-enabling game guide entries.
Space law is deliberately excluded pending a project-specific law decision.
"""

BOOK_SOURCE_PATHS = {
    'BookSecurity': ['guidebook/in_game/security/security'],
    'BookHowToRockAndStone': ['guidebook/in_game/cargo/salvage'],
}

DEFERRED_BOOKS = {'BookSpaceLaw': 'User requested leaving the law book unchanged'}
