# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

# The commander of a squad: what it thinks and decides (the feed of thoughts and the line of decisions in the NPC info panel).

soldier-name-and = and
soldier-relay-aloud = Relaying: { $text }

soldier-state-dead = dead
soldier-state-critical = in critical condition

soldier-alert-calm = calm
soldier-alert-suspicious = suspicious
soldier-alert-alert = ALERT
soldier-alert-evasion = searching for the enemy
soldier-alert-caution = cautious

soldier-stance-attack = attack
soldier-stance-hold = hold the ground
soldier-stance-withdraw = withdraw

# The line of current decisions
soldier-decision-alert = Alert: { $level }
soldier-decision-stance = Assessment: { $stance }
soldier-decision-reinforce = backup: { $count }
soldier-decision-intercept = intercept: { $count }
soldier-decision-search = search: soldiers { $count }, sectors { $cleared } of { $total }
soldier-decision-checks = noise checks: { $count }
soldier-decision-medic = medic at work: { $count }
soldier-decision-posts = post changes: { $count }
soldier-decision-push = push: soldiers { $count }
soldier-decision-hold = holding/cordon: soldiers { $count }
soldier-decision-sectors = sectors: { $count }
soldier-decision-supply = resupply: soldiers { $count }
soldier-decision-encircle = encirclement: soldiers { $count }, { $state }
soldier-decision-encircle-waiting = waiting until they are ready
soldier-decision-encircle-going = going in on the signal

# The thoughts of the commander: what it has learned
soldier-thought-assume-hq = HQ is on the net. Assuming command of the squad ({ $count } soldiers), asking for reports.
soldier-thought-assume-acting = { $name } takes the command: HQ is not on the net ({ $count } soldiers in the squad).
soldier-thought-contact = Contact: { $name } reports, hostiles in sight: { $count }, { $dir }, { $dist } m.
soldier-thought-contact-lost = { $name } has lost sight of the enemy { $dir }.
soldier-thought-enemy-down = { $name } has neutralized a target. Hostiles on the map: { $left }.
soldier-thought-noise-gunfire = { $name } hears gunfire { $dir }, { $dist } m.
soldier-thought-noise-explosion = { $name } hears an explosion { $dir }, { $dist } m.
soldier-thought-casualty = Casualty: { $who } ({ $state }) { $dir }, reported by { $name }.
soldier-thought-progress-cleared = { $name } reports: the sector is clear.
soldier-thought-progress-declined = { $name } cannot carry the order out: busy.
soldier-thought-silent = { $name } does not answer the orders. Not counting on him for now.

# The thoughts of the commander: what it has decided
soldier-thought-alert-suspicious = Something is off. I check first, then raise the squad.
soldier-thought-alert-raised = Alert! Enemy { $dir }, { $dist } m. Raising the squad.
soldier-thought-alert-evasion = The enemy is lost { $dir }. Combing the sectors around.
soldier-thought-alert-caution = Search is over. Alert called off, but we stay wary.
soldier-thought-alert-calm = All quiet. Putting the posts back to normal.
soldier-thought-rollcall = No word from some soldiers for a while ({ $count }). Asking for the situation.
soldier-thought-stance-attack = Forces: { $us } against { $them } - we press, suppression and flanking.
soldier-thought-stance-hold = Forces: { $us } against { $them } - we hold the ground, calling the backup.
soldier-thought-stance-withdraw = Forces: { $us } against { $them } - we can't take it, falling back and regrouping.
soldier-thought-reinforce = Backup to the contact: { $names }.
soldier-thought-role-suppress = { $name } suppresses the enemy with fire.
soldier-thought-role-flank = { $name } goes around the enemy.
soldier-thought-fallback = Falling back: { $names }.
soldier-thought-rally = Gathering around HQ: { $names }.
soldier-thought-intercept = The enemy moves { $dir }: { $names } to intercept.
soldier-thought-no-medic = No medic on the net: { $who } is left where he lies.
soldier-thought-medic = Medic { $name } - to { $who }.
soldier-thought-check-nobody = Nobody to send to check the noise.
soldier-thought-check = Checking the noise { $dir }, { $dist } m: { $names }.
soldier-thought-check-clear = Check over, all clear. { $names } return to the posts.
soldier-thought-sectors-clear = All the search sectors are checked.
soldier-thought-sector = Sector { $sector }: { $names }.
soldier-thought-post = Posts moved: { $names } take the position { $dir }.
soldier-thought-post-home = { $name } returns to the old post.
soldier-thought-sectors = Dividing the base into sectors: key places { $keys }, zones { $zones }, soldiers { $count }.
soldier-thought-push = Push into the room { $dir }: { $names } ({ $why }).
soldier-thought-why-witnesses = two soldiers see the enemy
soldier-thought-why-holds = the enemy holds his position
soldier-thought-encircle = Encircling the room { $dir }: the main group { $main } goes through the near door, { $flank } go around and come in from the other side ({ $why }). Entry on my signal.
soldier-thought-encircle-go = Both groups are at their doors ({ $why }). Giving the signal: we go in together.
soldier-thought-encircle-ready = everybody is ready
soldier-thought-encircle-late = time is up, not everybody is in place
soldier-thought-hold = Holding the entrance { $dir }: { $names }.
soldier-thought-cordon = Closing the exit { $dir }: { $names }.
soldier-thought-supply = To the supply crate { $dir }, { $dist } m: { $names }.
