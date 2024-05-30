﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Domain.Events;

public interface IEventPublisher
{
    void Publish(IDomainEvent domainEvent);
}
