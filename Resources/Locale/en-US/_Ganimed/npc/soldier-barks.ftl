# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

# Radio chatter of the soldiers. The $dir variable is a direction ("to the north", "nearby").

soldier-direction-north = to the north
soldier-direction-east = to the east
soldier-direction-south = to the south
soldier-direction-west = to the west
soldier-direction-unknown = nearby

# Suspicion and checking a noise
soldier-bark-heard-gunfire-1 = Gunfire { $dir }!
soldier-bark-heard-gunfire-2 = Shots fired { $dir }! All units, heads up!
soldier-bark-heard-gunfire-3 = Somebody is shooting { $dir }, we need to check it.
soldier-bark-heard-gunfire-4 = Gunshots { $dir }! That's not us.

soldier-bark-heard-explosion-1 = Explosion { $dir }! What was that?
soldier-bark-heard-explosion-2 = Something just blew up { $dir }! All units, heads up!
soldier-bark-heard-explosion-3 = There was a blast { $dir }, we need to check it.
soldier-bark-heard-explosion-4 = Heard a detonation { $dir }! This is not a drill.

soldier-bark-arrived-1 = On site. Looking around.
soldier-bark-arrived-2 = Arrived. Starting the search.
soldier-bark-arrived-3 = At the spot, checking the perimeter.

soldier-bark-all-clear-1 = Base, no hostiles detected.
soldier-bark-all-clear-2 = Search complete. Found nothing.
soldier-bark-all-clear-3 = Clear. No enemy found.
soldier-bark-all-clear-4 = Sector checked, no threats.

soldier-bark-clear-1 = Clear!
soldier-bark-clear-2 = Room clear.
soldier-bark-clear-3 = All clear here.

soldier-bark-returning-to-post-1 = Heading back to the post.
soldier-bark-returning-to-post-2 = Falling back to position.
soldier-bark-returning-to-post-3 = Back to the post, out.

# Contact and alert levels
soldier-bark-contact-1 = Contact { $dir }!
soldier-bark-contact-2 = I see a hostile { $dir }! Contact!
soldier-bark-contact-3 = Contact! Hostile { $dir }!
soldier-bark-contact-4 = Intruder { $dir }! Engaging!

soldier-bark-lost-target-1 = Lost the target!
soldier-bark-lost-target-2 = Hostile is out of sight!
soldier-bark-lost-target-3 = He's gone, I can't see him!

soldier-bark-evasion-1 = Combing the sector, stay sharp!
soldier-bark-evasion-2 = Searching, he's somewhere close.
soldier-bark-evasion-3 = Everybody search, eyes open!

soldier-bark-stand-down-1 = Alert is off. Stay alert anyway.
soldier-bark-stand-down-2 = Hostile not found. Standing down, but stay careful.
soldier-bark-stand-down-3 = Alert lowered. Don't relax.

soldier-bark-controlled-1 = Control!
soldier-bark-controlled-2 = Target neutralized, control.
soldier-bark-controlled-3 = Situation under control.

soldier-bark-man-down-1 = Man down! We lost a soldier!
soldier-bark-man-down-2 = We have casualties! Heads up!
soldier-bark-man-down-3 = One of ours is down! Be careful!

# Tactics
soldier-bark-reloading-1 = Reloading, cover me!
soldier-bark-reloading-2 = Magazine! Reloading!
soldier-bark-reloading-3 = Changing mags, cover!

soldier-bark-grenade-1 = Grenade!
soldier-bark-grenade-2 = Throwing a grenade, get down!
soldier-bark-grenade-3 = Grenade out!

soldier-bark-suppressing-1 = Covering fire, move!
soldier-bark-suppressing-2 = Suppressing! Go, go!
soldier-bark-suppressing-3 = Keeping him pinned!

soldier-bark-flanking-1 = Flanking!
soldier-bark-flanking-2 = Going around!
soldier-bark-flanking-3 = Moving to his side, cover me!

soldier-bark-entering-1 = Entering!
soldier-bark-entering-2 = Going in, cover me!
soldier-bark-entering-3 = Stacking up, ready!

soldier-bark-wounded-1 = I'm hit! Need help!
soldier-bark-wounded-2 = Took a hit! Falling back!
soldier-bark-wounded-3 = I'm wounded, cover me!

soldier-bark-healing-1 = Patching up, cover me!
soldier-bark-healing-2 = Applying a bandage, cover!
soldier-bark-healing-3 = Treating the wound, give me a minute!

# The medic
soldier-bark-call-medic-1 = Medic! Man down, get over here!
soldier-bark-call-medic-2 = Need a medic! One of ours is in critical condition!
soldier-bark-call-medic-3 = Medic, we have a man on the ground!

soldier-bark-medic-coming-1 = On my way to the wounded, cover me!
soldier-bark-medic-coming-2 = Medic moving up, hold on!
soldier-bark-medic-coming-3 = I see him, running!

soldier-bark-medic-dragging-1 = I've got him, pulling him out!
soldier-bark-medic-dragging-2 = Dragging him to cover, keep their heads down!
soldier-bark-medic-dragging-3 = Getting the wounded out of the line of fire!

soldier-bark-medic-treating-1 = Working, give me room!
soldier-bark-medic-treating-2 = Hold on, I'll have you on your feet in a moment!
soldier-bark-medic-treating-3 = Dressing the wounds, bear with me!

soldier-bark-medic-done-1 = He's stable!
soldier-bark-medic-done-2 = Back on his feet!
soldier-bark-medic-done-3 = Got him up, he can take it from here!

# The command: the reports of the soldiers and the answers of the commander. The words of the situation: $dir (where),
# $dist (how far, in meters), $names (whom an order is for), $who (whom it is about), $count (how many), $text (what is
# passed on).

soldier-bark-contact-many-1 = Contact { $dir }! I count { $count } hostiles!
soldier-bark-contact-many-2 = Hostile group { $dir }: { $count } of them!
soldier-bark-contact-many-3 = Multiple targets { $dir }, { $count } at least! Engaging!

soldier-bark-status-ready-1 = { $who }, on the net, I'm fine.
soldier-bark-status-ready-2 = { $who } at the post, all quiet.
soldier-bark-status-ready-3 = { $who } reporting: unhurt and ready.

soldier-bark-status-wounded-1 = { $who }, wounded, holding on.
soldier-bark-status-wounded-2 = { $who } reporting: hurt, but still going.
soldier-bark-status-wounded-3 = { $who }: I need help, but I'm on my feet.

soldier-bark-status-fighting-1 = { $who }, engaged!
soldier-bark-status-fighting-2 = { $who } is in a fight, don't distract me!
soldier-bark-status-fighting-3 = { $who } is busy, working the target!

soldier-bark-declined-1 = Can't do it, I'm busy!
soldier-bark-declined-2 = Unable, I'm in a fight!
soldier-bark-declined-3 = Negative, I'm tied up!

soldier-bark-ack-1 = Copy.
soldier-bark-ack-2 = Understood, on it.
soldier-bark-ack-3 = Roger!

soldier-bark-relay-1 = Relaying: { $text }
soldier-bark-relay-2 = For HQ, repeating: { $text }
soldier-bark-relay-3 = Passing on: { $text }

soldier-bark-link-lost-1 = Lost contact with HQ! Stick together!
soldier-bark-link-lost-2 = Comms are down, grouping up!
soldier-bark-link-lost-3 = HQ is silent! Everybody stay close, we talk by voice!

soldier-bark-order-alert-1 = All posts, alert! Hostile { $dir }, { $dist } meters.
soldier-bark-order-alert-2 = Attention all! Contact { $dir }, bringing the squad up.
soldier-bark-order-alert-3 = Alert! Enemy { $dir }, everybody stay ready.

soldier-bark-order-investigate-1 = { $names }, check { $dir }, { $dist } meters. The rest hold your posts.
soldier-bark-order-investigate-2 = { $names }, move out { $dir } and look the place over.
soldier-bark-order-investigate-3 = { $names }, go check { $dir }. No heroics.

soldier-bark-order-reinforce-1 = { $names }, backup needed! Contact { $dir }, { $dist } meters.
soldier-bark-order-reinforce-2 = { $names }, get to the contact { $dir } now!
soldier-bark-order-reinforce-3 = { $names }, support the fight { $dir }!

soldier-bark-order-search-1 = { $names }, sweep the sector { $dir }.
soldier-bark-order-search-2 = { $names }, search for the enemy { $dir }, { $dist } meters.
soldier-bark-order-search-3 = { $names }, comb { $dir }. Report in.

soldier-bark-order-intercept-1 = { $names }, intercept { $dir }! He's heading there.
soldier-bark-order-intercept-2 = { $names }, cut off the way { $dir }.
soldier-bark-order-intercept-3 = { $names }, meet him { $dir }.

soldier-bark-order-post-1 = { $names }, take the position { $dir }.
soldier-bark-order-post-2 = { $names }, to the post { $dir }, hold the sector.
soldier-bark-order-post-3 = { $names }, new position { $dir }, { $dist } meters.

soldier-bark-order-suppress-1 = { $names }, lay down fire, the rest move!
soldier-bark-order-suppress-2 = { $names }, suppress him!
soldier-bark-order-suppress-3 = { $names }, keep him pinned, don't let him peek!

soldier-bark-order-flank-1 = { $names }, flank him! Get on his side.
soldier-bark-order-flank-2 = { $names }, go around, we'll cover you.
soldier-bark-order-flank-3 = { $names }, come in from the side and finish it.

soldier-bark-order-fallback-1 = { $names }, fall back! Back to me, cover each other.
soldier-bark-order-fallback-2 = { $names }, pull back, don't walk into the fire!
soldier-bark-order-fallback-3 = { $names }, break contact and withdraw!

soldier-bark-order-assault-1 = { $names }, cancel the withdrawal! Back on the enemy.
soldier-bark-order-assault-2 = { $names }, back into the fight!
soldier-bark-order-assault-3 = { $names }, hold the ground, open fire!

soldier-bark-order-medic-1 = { $names }, { $who } is down { $dir }, { $dist } meters. Get to him!
soldier-bark-order-medic-2 = Medic { $names }, wounded { $who } { $dir }, get him out!
soldier-bark-order-medic-3 = { $names }, go to { $who } { $dir } at once!

soldier-bark-roll-call-1 = All posts, report your status.
soldier-bark-roll-call-2 = { $names }, report in, how do you copy?
soldier-bark-roll-call-3 = Roll call! Everybody sound off.

soldier-bark-ack-report-1 = Copy, { $who }.
soldier-bark-ack-report-2 = { $who }, understood.
soldier-bark-ack-report-3 = Got you, { $who }. Working on it.

soldier-bark-assume-command-1 = No word from HQ. { $who } is taking command!
soldier-bark-assume-command-2 = I'm taking command. Listen to my orders!
soldier-bark-assume-command-3 = { $who } commanding! Report to me.

soldier-bark-hq-ack-report-1 = HQ copies, { $who }.
soldier-bark-hq-ack-report-2 = { $who }, HQ hears you.
soldier-bark-hq-ack-report-3 = HQ understood, { $who }. Carry on.

soldier-bark-hq-assume-command-1 = HQ is on the net. Assuming command. Report your status.
soldier-bark-hq-assume-command-2 = HQ speaking. The squad is under my command.
soldier-bark-hq-assume-command-3 = HQ on the air. Orders come from me only.

soldier-bark-order-sectors-1 = All posts, spreading out to the sectors. Hold your zone.
soldier-bark-order-sectors-2 = Take your sectors as planned. Everybody to your place.
soldier-bark-order-sectors-3 = HQ is assigning the sectors. Take your posts, keep your eyes open.

soldier-bark-order-push-1 = { $names }, push! Hostile { $dir }, { $dist } meters. Going in!
soldier-bark-order-push-2 = { $names }, press him! The enemy is dug in { $dir } — we assault.
soldier-bark-order-push-3 = { $names }, assault { $dir }, { $dist } meters. Enter when ready!

soldier-bark-order-hold-1 = { $names }, hold the entrance { $dir }. Let nobody out.
soldier-bark-order-hold-2 = { $names }, take the passage { $dir } and hold it.
soldier-bark-order-hold-3 = { $names }, hold the corridor { $dir }, don't fall back without orders.

soldier-bark-order-cordon-1 = { $names }, block the exits { $dir }!
soldier-bark-order-cordon-2 = { $names }, cordon { $dir }: let nobody out.
soldier-bark-order-cordon-3 = { $names }, seal the doors { $dir }, he must not get away.

soldier-bark-order-resupply-1 = { $names }, resupply — crate { $dir }, { $dist } meters.
soldier-bark-order-resupply-2 = { $names }, to the supply crate { $dir }. Take what you need.
soldier-bark-order-resupply-3 = { $names }, to the stores { $dir }, { $dist } meters. Restock and get back.

soldier-bark-resupplying-1 = Taking supplies.
soldier-bark-resupplying-2 = Restocking, cover me.
soldier-bark-resupplying-3 = Restocking.

soldier-bark-restocked-1 = Restocked.
soldier-bark-restocked-2 = Got my supplies, ready.
soldier-bark-restocked-3 = Ammo is good.

soldier-bark-need-supply-1 = { $who }, low on ammo and bandages, need a resupply.
soldier-bark-need-supply-2 = { $who }, supplies are running out. Need restocking.
soldier-bark-need-supply-3 = { $who }, requesting a resupply: ammo and medical are almost gone.

soldier-bark-order-encircle-1 = { $names }, encircle! Hostile { $dir }, { $dist } meters. Groups to the doors, wait for the signal!
soldier-bark-order-encircle-2 = { $names }, we go in from two sides — hostile { $dir }. Take the doors and wait for my call.
soldier-bark-order-encircle-3 = { $names }, going around him { $dir }, { $dist } meters. To the doors, we enter on my signal!

soldier-bark-order-go-1 = { $names }, go, go! Entering!
soldier-bark-order-go-2 = { $names }, both groups in place — go, go in!
soldier-bark-order-go-3 = { $names }, on my signal — entering now!

soldier-bark-ready-1 = { $who }, at the door, ready.
soldier-bark-ready-2 = { $who } in position, waiting for the signal.
soldier-bark-ready-3 = { $who }, door covered. Ready to go in.
