﻿// This file is Part of CalDavSynchronizer (http://outlookcaldavsynchronizer.sourceforge.net/)
// Copyright (c) 2015 Gerhard Zehetbauer 
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public LicenseIICalendar
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CalDavSynchronizer.Generic.EntityMapping;
using DDay.iCal;
using log4net;
using Microsoft.Office.Interop.Outlook;
using ICalAttachment = DDay.iCal.Attachment;
using OutlookAttachment = Microsoft.Office.Interop.Outlook.Attachment;
using RecurrencePattern = DDay.iCal.RecurrencePattern;

namespace CalDavSynchronizer.Implementation
{
  internal class AppointmentEventEntityMapper : IEntityMapper<AppointmentItem, IICalendar>
  {
    private static readonly ILog s_logger = LogManager.GetLogger (MethodInfo.GetCurrentMethod().DeclaringType);

    private const string PR_SMTP_ADDRESS = "http://schemas.microsoft.com/mapi/proptag/0x39FE001E";
    private readonly string _outlookEmailAddress;
    private readonly Uri _serverEmailAddress;

    public AppointmentEventEntityMapper (string outlookEmailAddress, Uri serverEmailAddress)
    {
      _outlookEmailAddress = outlookEmailAddress;
      _serverEmailAddress = serverEmailAddress;
    }


    public IICalendar Map1To2 (AppointmentItem source, IICalendar targetCalender)
    {
      IEvent target = new Event();
      targetCalender.Events.Add (target);

      if (source.AllDayEvent)
      {
        // Outlook's AllDayEvent relates to Start and not not StartUtc!!!
        target.Start = new iCalDateTime (source.Start);
        target.Start.HasTime = false;

        target.End = new iCalDateTime (source.End);
        target.End.HasTime = false;
        target.IsAllDay = true;
      }
      else
      {
        target.Start = new iCalDateTime (source.StartUTC) { IsUniversalTime = true };
        target.DTEnd = new iCalDateTime (source.EndUTC) { IsUniversalTime = true };
        target.IsAllDay = false;
      }

      target.Summary = source.Subject;
      target.Location = source.Location;
      target.Description = source.Body;

      target.Priority = MapPriority1To2 (source.Importance);

      bool organizerSet;
      MapAttendees1To2 (source, target, out organizerSet);
      MapRecurrance1To2 (source, target);
      if (!organizerSet)
        MapOrganizer1To2 (source, target);

      target.Class = MapPrivacy1To2 (source.Sensitivity);
      MapReminder1To2 (source, target);

      MapCategories1To2 (source, target);

      target.Transparency = MapTransparency1To2 (source.BusyStatus);

      return targetCalender;
    }

    private TransparencyType MapTransparency1To2 (OlBusyStatus value)
    {
      switch (value)
      {
        case OlBusyStatus.olBusy:
        case OlBusyStatus.olOutOfOffice:
        case OlBusyStatus.olWorkingElsewhere:
          return TransparencyType.Opaque;
        case OlBusyStatus.olTentative:
        case OlBusyStatus.olFree:
          return TransparencyType.Transparent;
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", value));
    }


    private OlBusyStatus MapTransparency2To1 (TransparencyType value)
    {
      switch (value)
      {
        case TransparencyType.Opaque:
          return OlBusyStatus.olBusy;
        case TransparencyType.Transparent:
          return OlBusyStatus.olFree;
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", value));
    }


    private static void MapCategories1To2 (AppointmentItem source, IEvent target)
    {
      if (!string.IsNullOrEmpty (source.Categories))
      {
        Array.ForEach (
            source.Categories.Split (new[] { CultureInfo.CurrentCulture.TextInfo.ListSeparator }, StringSplitOptions.RemoveEmptyEntries),
            c => target.Categories.Add (c)
            );
      }
    }

    private void MapReminder1To2 (AppointmentItem source, IEvent target)
    {
      if (source.ReminderSet)
      {
        var trigger = new Trigger (TimeSpan.FromMinutes (-source.ReminderMinutesBeforeStart));
        trigger.Parameters.Add ("VALUE", "DURATION");
        target.Alarms.Add (
            new Alarm()
            {
                Action = AlarmAction.Display,
                Trigger = trigger
            }
            );
      }
    }

    private void MapReminder2To1 (IEvent source, AppointmentItem target)
    {
      if (source.Alarms.Count == 0)
      {
        target.ReminderSet = false;
        return;
      }

      if (source.Alarms.Count > 1)
        s_logger.WarnFormat ("Event '{0}' contains multiple alarms. Ignoring all except first.", source.Url);

      var alarm = source.Alarms[0];

      if (!(alarm.Trigger.IsRelative && alarm.Trigger.Related == TriggerRelation.Start && alarm.Trigger.Duration < TimeSpan.Zero && alarm.Trigger.Duration.HasValue))
      {
        s_logger.WarnFormat ("Event '{0}' alarm is not relative before event start. Ignoring.", source.Url);
        target.ReminderSet = false;
        return;
      }

      target.ReminderSet = true;
      target.ReminderMinutesBeforeStart = -(int) alarm.Trigger.Duration.Value.TotalMinutes;
    }

    private string MapPrivacy1To2 (OlSensitivity value)
    {
      switch (value)
      {
        case OlSensitivity.olNormal:
          return "PUBLIC";
        case OlSensitivity.olPersonal:
          return "PRIVATE"; // not sure
        case OlSensitivity.olPrivate:
          return "PRIVATE";
        case OlSensitivity.olConfidential:
          return "CONFIDENTIAL";
      }
      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", value));
    }

    private OlSensitivity MapPrivacy2To1 (string value)
    {
      switch (value)
      {
        case "PUBLIC":
          return OlSensitivity.olNormal;
        case "PRIVATE":
          return OlSensitivity.olPrivate;
        case "CONFIDENTIAL":
          return OlSensitivity.olConfidential;
      }
      return OlSensitivity.olNormal;
    }


    private void MapOrganizer1To2 (AppointmentItem source, IEvent target)
    {
      var organizer = source.GetOrganizer();
      if (organizer != null)
      {
        SetOrganizer (target, organizer);
      }
    }

    private void SetOrganizer (IEvent target, AddressEntry organizer)
    {
      var targetOrganizer = new Organizer (GetMailUrl (organizer));
      targetOrganizer.CommonName = organizer.Name;
      target.Organizer = targetOrganizer;
    }


    private string GetMailUrl (AddressEntry addressEntry)
    {
      string emailAddress;

      if (addressEntry.AddressEntryUserType == OlAddressEntryUserType.olSmtpAddressEntry)
        emailAddress = addressEntry.Address;
      else
        emailAddress = addressEntry.PropertyAccessor.GetProperty (PR_SMTP_ADDRESS);

      return string.Format ("MAILTO:{0}", emailAddress);
    }

    private void MapRecurrance1To2 (AppointmentItem source, IEvent target)
    {
      if (source.IsRecurring)
      {
        var sourceRecurrencePattern = source.GetRecurrencePattern();

        IRecurrencePattern targetRecurrencePattern = new RecurrencePattern();
        if (!sourceRecurrencePattern.NoEndDate)
        {
          targetRecurrencePattern.Count = sourceRecurrencePattern.Occurrences;
          targetRecurrencePattern.Until = sourceRecurrencePattern.PatternEndDate;
        }
        targetRecurrencePattern.Interval = sourceRecurrencePattern.Interval;

        switch (sourceRecurrencePattern.RecurrenceType)
        {
          case OlRecurrenceType.olRecursDaily:
            targetRecurrencePattern.Frequency = FrequencyType.Daily;
            break;
          case OlRecurrenceType.olRecursWeekly:
            targetRecurrencePattern.Frequency = FrequencyType.Weekly;
            MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay, 0);
            break;
          case OlRecurrenceType.olRecursMonthly:
            targetRecurrencePattern.Frequency = FrequencyType.Monthly;
            targetRecurrencePattern.ByMonthDay.Add (sourceRecurrencePattern.DayOfMonth);
            break;
          case OlRecurrenceType.olRecursMonthNth:
            targetRecurrencePattern.Frequency = FrequencyType.Monthly;
            MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay, sourceRecurrencePattern.Instance);
            targetRecurrencePattern.ByWeekNo.Add (sourceRecurrencePattern.Instance);
            break;
          case OlRecurrenceType.olRecursYearly:
            targetRecurrencePattern.Frequency = FrequencyType.Yearly;
            targetRecurrencePattern.ByMonthDay.Add (sourceRecurrencePattern.DayOfMonth);
            targetRecurrencePattern.ByMonth.Add (sourceRecurrencePattern.MonthOfYear);
            break;
          case OlRecurrenceType.olRecursYearNth:
            targetRecurrencePattern.Frequency = FrequencyType.Yearly;
            MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay, sourceRecurrencePattern.Instance);
            targetRecurrencePattern.ByMonth.Add (sourceRecurrencePattern.MonthOfYear);
            targetRecurrencePattern.ByWeekNo.Add (sourceRecurrencePattern.Instance);
            break;
        }

        target.RecurrenceRules.Add (targetRecurrencePattern);
      }
    }

    private void MapDayOfWeek1To2 (OlDaysOfWeek source, IList<IWeekDay> target, int offset)
    {
      if ((source & OlDaysOfWeek.olMonday) > 0)
        target.Add (new WeekDay (DayOfWeek.Monday, offset));
      if ((source & OlDaysOfWeek.olTuesday) > 0)
        target.Add (new WeekDay (DayOfWeek.Tuesday, offset));
      if ((source & OlDaysOfWeek.olWednesday) > 0)
        target.Add (new WeekDay (DayOfWeek.Wednesday, offset));
      if ((source & OlDaysOfWeek.olThursday) > 0)
        target.Add (new WeekDay (DayOfWeek.Thursday, offset));
      if ((source & OlDaysOfWeek.olFriday) > 0)
        target.Add (new WeekDay (DayOfWeek.Friday, offset));
      if ((source & OlDaysOfWeek.olSaturday) > 0)
        target.Add (new WeekDay (DayOfWeek.Saturday, offset));
      if ((source & OlDaysOfWeek.olSunday) > 0)
        target.Add (new WeekDay (DayOfWeek.Sunday, offset));
    }

    private OlDaysOfWeek MapDayOfWeek2To1 (IList<IWeekDay> source)
    {
      OlDaysOfWeek target = 0;

      foreach (var day in source)
      {
        switch (day.DayOfWeek)
        {
          case DayOfWeek.Monday:
            target |= OlDaysOfWeek.olMonday;
            break;
          case DayOfWeek.Tuesday:
            target |= OlDaysOfWeek.olTuesday;
            break;
          case DayOfWeek.Wednesday:
            target |= OlDaysOfWeek.olWednesday;
            break;
          case DayOfWeek.Thursday:
            target |= OlDaysOfWeek.olThursday;
            break;
          case DayOfWeek.Friday:
            target |= OlDaysOfWeek.olFriday;
            break;
          case DayOfWeek.Saturday:
            target |= OlDaysOfWeek.olSaturday;
            break;
          case DayOfWeek.Sunday:
            target |= OlDaysOfWeek.olSunday;
            break;
        }
      }
      return target;
    }


    private void MapRecurrance2To1 (IEvent source, AppointmentItem target)
    {
      target.ClearRecurrencePattern();

      if (source.RecurrenceRules.Count > 0)
      {
        var targetRecurrencePattern = target.GetRecurrencePattern();
        if (source.RecurrenceRules.Count > 1)
        {
          s_logger.WarnFormat ("Event '{0}' contains more than one recurrence rule. Since outlook supports only one rule, all except the first one will be ignored.", source.Url);
        }
        var sourceRecurrencePattern = source.RecurrenceRules[0];

        targetRecurrencePattern.Interval = sourceRecurrencePattern.Interval;

        if (sourceRecurrencePattern.Count >= 0)
          targetRecurrencePattern.Occurrences = sourceRecurrencePattern.Count;

        if (sourceRecurrencePattern.Until != default(DateTime))
          targetRecurrencePattern.PatternEndDate = sourceRecurrencePattern.Until;

        switch (sourceRecurrencePattern.Frequency)
        {
          case FrequencyType.Daily:
            targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursDaily;
            break;
          case FrequencyType.Weekly:
            if (sourceRecurrencePattern.ByDay.Count > 0)
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursWeekly;
              targetRecurrencePattern.DayOfWeekMask = MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
            }
            else
            {
              s_logger.WarnFormat ("Event '{0}' contains a weekly recurrence pattern, which is not supported by outlook. Ignoring recurrence rule.", source.Url);
              target.ClearRecurrencePattern();
            }
            break;
          case FrequencyType.Monthly:
            if (sourceRecurrencePattern.ByDay.Count > 0)
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursMonthNth;
              if (sourceRecurrencePattern.ByWeekNo.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one week in a monthly recurrence rule. Since outlook supports only one week, all except the first one will be ignored.", source.Url);
              }
              else if (sourceRecurrencePattern.ByWeekNo.Count > 0)
              {
                targetRecurrencePattern.Instance = sourceRecurrencePattern.ByWeekNo[0];
              }
              else
              {
                targetRecurrencePattern.Instance = (sourceRecurrencePattern.ByDay[0].Offset >= 0) ? sourceRecurrencePattern.ByDay[0].Offset : 5;
              }
              targetRecurrencePattern.DayOfWeekMask = MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
            }
            else if (sourceRecurrencePattern.ByMonthDay.Count > 0)
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursMonthly;
              if (sourceRecurrencePattern.ByMonthDay.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one days in a monthly recurrence rule. Since outlook supports only one day, all except the first one will be ignored.", source.Url);
              }
              targetRecurrencePattern.DayOfMonth = sourceRecurrencePattern.ByMonthDay[0];
            }
            else
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursMonthly;
            }
            break;
          case FrequencyType.Yearly:
            if (sourceRecurrencePattern.ByMonth.Count > 0 && sourceRecurrencePattern.ByWeekNo.Count > 0)
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearNth;
              if (sourceRecurrencePattern.ByMonth.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.", source.Url);
              }
              targetRecurrencePattern.MonthOfYear = sourceRecurrencePattern.ByMonth[0];

              if (sourceRecurrencePattern.ByWeekNo.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one week in a yearly recurrence rule. Since outlook supports only one week, all except the first one will be ignored.", source.Url);
              }
              targetRecurrencePattern.Instance = sourceRecurrencePattern.ByWeekNo[0];

              targetRecurrencePattern.DayOfWeekMask = MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
            }
            else if (sourceRecurrencePattern.ByMonth.Count > 0 && sourceRecurrencePattern.ByMonthDay.Count > 0)
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearly;
              if (sourceRecurrencePattern.ByMonth.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.", source.Url);
              }
              targetRecurrencePattern.MonthOfYear = sourceRecurrencePattern.ByMonth[0];

              if (sourceRecurrencePattern.ByMonthDay.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one days in a monthly recurrence rule. Since outlook supports only one day, all except the first one will be ignored.", source.Url);
              }
              targetRecurrencePattern.DayOfMonth = sourceRecurrencePattern.ByMonthDay[0];
            }
            else if (sourceRecurrencePattern.ByMonth.Count > 0 && sourceRecurrencePattern.ByDay.Count > 0)
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearNth;
              if (sourceRecurrencePattern.ByMonth.Count > 1)
              {
                s_logger.WarnFormat ("Event '{0}' contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.", source.Url);
              }
              targetRecurrencePattern.MonthOfYear = sourceRecurrencePattern.ByMonth[0];

              targetRecurrencePattern.Instance = (sourceRecurrencePattern.ByDay[0].Offset >= 0) ? sourceRecurrencePattern.ByDay[0].Offset : 5;
              targetRecurrencePattern.DayOfWeekMask = MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
            }
            else
            {
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearly;
            }
            break;
          default:
            s_logger.WarnFormat ("Recurring event '{0}' contains the Frequency '{1}', which is not supported by outlook. Ignoring recurrence rule.", source.Url, sourceRecurrencePattern.Frequency);
            target.ClearRecurrencePattern();
            break;
        }
      }
    }

    private int MapPriority1To2 (OlImportance value)
    {
      switch (value)
      {
        case OlImportance.olImportanceLow:
          return 9;
        case OlImportance.olImportanceNormal:
          return 5;
        case OlImportance.olImportanceHigh:
          return 1;
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", value));
    }

    private OlImportance MapPriority2To1 (int value)
    {
      switch (value)
      {
        case 9:
          return OlImportance.olImportanceLow;
        case 0:
        case 5:
          return OlImportance.olImportanceNormal;
        case 1:
          return OlImportance.olImportanceHigh;
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", value));
    }


    private void MapAttendees1To2 (AppointmentItem source, IEvent target, out bool organizerSet)
    {
      organizerSet = false;

      foreach (Recipient recipient in source.Recipients)
      {
        if (!IsOwnIdentity (recipient))
        {
          Attendee attendee;

          if (!string.IsNullOrEmpty (recipient.Address))
            attendee = new Attendee (GetMailUrl (recipient.AddressEntry));
          else
            attendee = new Attendee();

          attendee.CommonName = recipient.Name;
          attendee.Role = MapAttendeeType1To2 ((OlMeetingRecipientType) recipient.Type);
          target.Attendees.Add (attendee);
        }
        if (((OlMeetingRecipientType) recipient.Type) == OlMeetingRecipientType.olOrganizer)
        {
          SetOrganizer (target, recipient.AddressEntry);
          organizerSet = true;
        }
      }
    }

    private bool IsOwnIdentity (Recipient recipient)
    {
      return StringComparer.InvariantCultureIgnoreCase.Compare (recipient.Address, _outlookEmailAddress) == 0;
    }


    public string MapAttendeeType1To2 (OlMeetingRecipientType recipientType)
    {
      switch (recipientType)
      {
        case OlMeetingRecipientType.olOptional:
          return "OPT-PARTICIPANT";
        case OlMeetingRecipientType.olRequired:
          return "REQ-PARTICIPANT";
        case OlMeetingRecipientType.olResource:
          return "CHAIR";
        case OlMeetingRecipientType.olOrganizer:
          return "REQ-PARTICIPANT";
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", recipientType));
    }

    public OlMeetingRecipientType MapAttendeeType2To1 (string recipientType)
    {
      switch (recipientType)
      {
        case null:
        case "NON-PARTICIPANT":
        case "OPT-PARTICIPANT":
          return OlMeetingRecipientType.olOptional;
        case "REQ-PARTICIPANT":
          return OlMeetingRecipientType.olRequired;
        case "CHAIR":
          return OlMeetingRecipientType.olResource;
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", recipientType));
    }


    private const int s_mailtoSchemaLength = 7; // length of "mailto:"

    public AppointmentItem Map2To1 (IICalendar sourceCalendar, AppointmentItem target)
    {
      var source = sourceCalendar.Events[0];

      if (source.IsAllDay)
      {
        target.Start = source.Start.Value;
        target.End = source.End.Value;
        target.AllDayEvent = true;
      }
      else
      {
        target.AllDayEvent = false;
        target.StartUTC = source.Start.UTC;
        if (source.DTEnd != null)
        {
          target.EndUTC = source.DTEnd.UTC;
        }
        else if (source.Start.HasTime)
        {
          target.EndUTC = source.Start.UTC;
        }
        else
        {
          target.EndUTC = source.Start.AddDays (1).UTC;
        }
      }


      target.Subject = source.Summary;
      target.Location = source.Location;
      target.Body = source.Description;

      target.Importance = MapPriority2To1 (source.Priority);

      MapAttendees2To1 (source, target);
      if (source.Organizer != null)
      {
        target.MeetingStatus = OlMeetingStatus.olMeetingReceived;
        target.PropertyAccessor.SetProperty ("http://schemas.microsoft.com/mapi/proptag/0x0042001F", source.Organizer.Value.ToString().Substring (s_mailtoSchemaLength));
      }
      else
      {
        target.MeetingStatus = OlMeetingStatus.olNonMeeting;
      }
      MapRecurrance2To1 (source, target);

      target.Sensitivity = MapPrivacy2To1 (source.Class);
      MapReminder2To1 (source, target);

      MapCategories2To1 (source, target);

      target.BusyStatus = MapTransparency2To1 (source.Transparency);

      return target;
    }

    private static void MapCategories2To1 (IEvent source, AppointmentItem target)
    {
      target.Categories = string.Join (CultureInfo.CurrentCulture.TextInfo.ListSeparator, source.Categories);
    }


    private void MapAttendees2To1 (IEvent source, AppointmentItem target)
    {
      var targetRecipientsWhichShouldRemain = new HashSet<Recipient>();
      var indexByEmailAddresses = GetOutlookRecipientsByEmailAddressesOrName (target);

      foreach (var attendee in source.Attendees)
      {
        Recipient targetRecipient = null;

        if (attendee.Value != null && !string.IsNullOrEmpty (attendee.Value.ToString().Substring (s_mailtoSchemaLength)))
        {
          if (!indexByEmailAddresses.TryGetValue (attendee.Value.ToString(), out targetRecipient))
          {
            targetRecipient = target.Recipients.Add (attendee.Value.ToString().Substring (s_mailtoSchemaLength));
          }
        }
        else
        {
          if (!string.IsNullOrEmpty (attendee.CommonName))
            targetRecipient = target.Recipients.Add (attendee.CommonName);
        }

        if (targetRecipient != null)
        {
          targetRecipientsWhichShouldRemain.Add (targetRecipient);
          targetRecipient.Type = (int) MapAttendeeType2To1 (attendee.Role);
        }
      }

      for (int i = target.Recipients.Count; i > 0; i--)
      {
        var recipient = target.Recipients[i];
        if (!IsOwnIdentity (recipient))
        {
          if (!targetRecipientsWhichShouldRemain.Contains (recipient))
            target.Recipients.Remove (i);
        }
      }
    }

    private Dictionary<string, Recipient> GetOutlookRecipientsByEmailAddressesOrName (AppointmentItem appointment)
    {
      Dictionary<string, Recipient> indexByEmailAddresses = new Dictionary<string, Recipient> (StringComparer.InvariantCultureIgnoreCase);

      foreach (Recipient recipient in appointment.Recipients)
      {
        if (! string.IsNullOrEmpty (recipient.Address))
          indexByEmailAddresses[GetMailUrl (recipient.AddressEntry)] = recipient;
        else
          indexByEmailAddresses[recipient.Name] = recipient;
      }

      return indexByEmailAddresses;
    }
  }
}